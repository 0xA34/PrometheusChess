using ChessCore.Security;
using PrometheusServer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrometheusServer.Core;
using PrometheusServer.Data;
using PrometheusServer.Logging;

namespace PrometheusServer;

public sealed class CommandLineOptions
{
    public bool DatabaseMode { get; set; }
    public bool ShowHelp { get; set; }
}

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var options = ParseCommandLineArgs(args);

        if (options.ShowHelp)
        {
            DisplayHelp();
            return;
        }

        if (!options.DatabaseMode)
        {
            DisplayInMemoryModeBanner();
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var services = ConfigureServices(configuration, options);
        var serviceProvider = services.BuildServiceProvider();

        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

        try
        {
            var dbConfig = serviceProvider.GetRequiredService<DatabaseConfiguration>();
            if (!dbConfig.UseInMemory)
            {
                var dbService = serviceProvider.GetRequiredService<DatabaseService>();
                logger.LogInformation("Testing database connection...");

                if (!await dbService.TestConnectionAsync())
                {
                    logger.LogError("Failed to connect to database. Check your configuration.");
                    Environment.ExitCode = 1;
                    return;
                }

                var version = await dbService.GetServerVersionAsync();
                logger.LogInformation("Connected to PostgreSQL: {Version}", version);
            }
            else
            {
                logger.LogWarning("Running in IN-MEMORY mode. Data will not be persisted!");
            }

            var server = serviceProvider.GetRequiredService<PrometheusGameServer>();
            var serverConfig = serviceProvider.GetRequiredService<ServerConfiguration>();

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                ServerLogger.LogServerShutdown(logger);
                cts.Cancel();
            };

            await server.StartAsync();

            ServerLogger.LogServerStartup(
                logger,
                serverConfig.BindAddress,
                serverConfig.Port,
                serverConfig.MaxConnections);

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("  Press Ctrl+C to stop the server.");
            Console.WriteLine();
            Console.ResetColor();

            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }

            await server.StopAsync();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  Server shutdown complete.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            ServerLogger.LogErrorEvent(logger, "Server Startup", "Failed to start or crashed", ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            if (serviceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static IServiceCollection ConfigureServices(IConfiguration configuration, CommandLineOptions options)
    {
        var services = new ServiceCollection();

        services.AddSingleton(configuration);

        var logLevel = options.DatabaseMode
            ? configuration.GetValue("Logging:LogLevel:Default", LogLevel.Information)
            : LogLevel.Debug;

        services.AddLogging(builder =>
        {
            if (options.DatabaseMode)
            {
                builder.AddConfiguration(configuration.GetSection("Logging"));
            }

            builder.AddColoredConsole(opts =>
            {
                opts.TimestampFormat = "HH:mm:ss.fff";
                opts.UseUtcTimestamp = false;
            });

            builder.SetMinimumLevel(logLevel);
        });

        var serverConfig = new ServerConfiguration
        {
            Port = configuration.GetValue("Server:Port", 8787),
            BindAddress = configuration.GetValue("Server:BindAddress", "0.0.0.0") ?? "0.0.0.0",
            MaxConnections = configuration.GetValue("Server:MaxConnections", 1000),
            HeartbeatIntervalSeconds = configuration.GetValue("Server:HeartbeatIntervalSeconds", 30),
            ConnectionTimeoutSeconds = configuration.GetValue("Server:ConnectionTimeoutSeconds", 120),
            MaxRequestsPerMinute = options.DatabaseMode
                ? configuration.GetValue("Security:MaxRequestsPerMinute", 100)
                : 1000,
            DisconnectionGracePeriodSeconds = configuration.GetValue("Game:DisconnectionGracePeriodSeconds", 60)
        };
        services.AddSingleton(serverConfig);

        string jwtSecret;
        if (options.DatabaseMode)
        {
            jwtSecret = configuration.GetValue<string>("Security:JwtSecretKey")
                ?? "DefaultSecretKey_CHANGE_THIS_IN_PRODUCTION_12345!";
        }
        else
        {
            jwtSecret = $"InMemory_AutoGenerated_Secret_{Guid.NewGuid():N}";
        }

        if (options.DatabaseMode && jwtSecret.Contains("CHANGE_THIS"))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  WARNING: Using default JWT secret key. Change this in production!");
            Console.WriteLine();
            Console.ResetColor();
        }

        var securityManager = new SecurityManager(
            jwtSecret,
            configuration.GetValue("Security:Issuer", "PrometheusServer") ?? "PrometheusServer",
            configuration.GetValue("Security:Audience", "PrometheusVulkan") ?? "PrometheusVulkan",
            configuration.GetValue("Security:TokenExpirationHours", 24)
        );
        services.AddSingleton(securityManager);

        var dbConfig = new DatabaseConfiguration
        {
            Host = configuration.GetValue("Database:Host", "localhost") ?? "localhost",
            Port = configuration.GetValue("Database:Port", 5432),
            Database = configuration.GetValue("Database:Database", "chess_game") ?? "chess_game",
            Username = configuration.GetValue("Database:Username", "chess_server") ?? "chess_server",
            Password = Environment.GetEnvironmentVariable("CHESS_DB_PASSWORD")
                       ?? configuration.GetValue<string>("Database:Password")
                       ?? string.Empty,
            SslMode = configuration.GetValue("Database:SslMode", "Prefer") ?? "Prefer",
            UseInMemory = !options.DatabaseMode
        };

        if (!dbConfig.UseInMemory)
        {
            var (isValid, errorMessage) = dbConfig.Validate();
            if (!isValid)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  Database configuration error: {errorMessage}");
                Console.WriteLine("  Set the password via CHESS_DB_PASSWORD environment variable or in appsettings.json");
                Console.WriteLine();
                Console.ResetColor();
                throw new InvalidOperationException($"Database configuration error: {errorMessage}");
            }
        }

        services.AddSingleton(dbConfig);

        services.AddSingleton<DatabaseService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DatabaseService>>();
            var config = sp.GetRequiredService<DatabaseConfiguration>();
            return new DatabaseService(logger, config);
        });

        services.AddSingleton<PlayerManager>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PlayerManager>>();
            var databaseService = sp.GetRequiredService<DatabaseService>();
            var dbConfig = sp.GetRequiredService<DatabaseConfiguration>();
            return new PlayerManager(
                logger,
                databaseService,
                dbConfig,
                configuration.GetValue("Rating:DefaultRating", 1200),
                configuration.GetValue("Rating:KFactor", 32),
                configuration.GetValue("Rating:MinRating", 100),
                configuration.GetValue("Rating:MaxRating", 3000)
            );
        });

        services.AddSingleton<SessionManager>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SessionManager>>();
            var databaseService = sp.GetRequiredService<DatabaseService>();
            var dbConfig = sp.GetRequiredService<DatabaseConfiguration>();
            var securityManager = sp.GetRequiredService<SecurityManager>();
            return new SessionManager(
                logger,
                databaseService,
                dbConfig,
                securityManager,
                configuration.GetValue("Security:TokenExpirationHours", 24),
                configuration.GetValue("Security:MaxSessionsPerPlayer", 5)
            );
        });

        services.AddSingleton<MatchmakingService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<MatchmakingService>>();
            return new MatchmakingService(
                logger,
                configuration.GetValue("Matchmaking:DefaultRatingRange", 200),
                configuration.GetValue("Matchmaking:MaxRatingRange", 500),
                configuration.GetValue("Matchmaking:RatingExpansionIntervalSeconds", 30),
                configuration.GetValue("Matchmaking:RatingExpansionAmount", 50)
            );
        });

        services.AddSingleton<GameManager>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<GameManager>>();
            var playerManager = sp.GetRequiredService<PlayerManager>();
            var databaseService = sp.GetRequiredService<DatabaseService>();
            var dbConfig = sp.GetRequiredService<DatabaseConfiguration>();
            return new GameManager(logger, playerManager, databaseService, dbConfig);
        });

        services.AddSingleton<PrometheusGameServer>();

        return services;
    }

    private static CommandLineOptions ParseCommandLineArgs(string[] args)
    {
        var options = new CommandLineOptions();

        foreach (var arg in args)
        {
            var lowerArg = arg.ToLowerInvariant();

            switch (lowerArg)
            {
                case "--database":
                case "--db":
                case "-p":
                    options.DatabaseMode = true;
                    break;

                case "--help":
                case "-h":
                case "-?":
                    options.ShowHelp = true;
                    break;
            }
        }

        return options;
    }

    private static void DisplayHelp()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Prometheus Server");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("  A chess server, heavily rely on the server. Just a small project, nothing too special.");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  USAGE:");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("      Prometheus [OPTIONS]");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  OPTIONS:");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("      --database, --db, -p        Run in database mode (PostgreSQL)");
        Console.WriteLine("                                  - Persistent storage");
        Console.WriteLine("                                  - Production logging levels");
        Console.WriteLine("                                  - Requires database configuration");
        Console.WriteLine();
        Console.WriteLine("      --help, -h, -?              Show this help message");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  EXAMPLES:");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("      Prometheus                 Run in-memory mode (default)");
        Console.WriteLine("      Prometheus --db            Run with PostgreSQL database");
        Console.WriteLine();

        Console.ResetColor();
    }

    private static void DisplayInMemoryModeBanner()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  DEVELOPMENT MODE");
        Console.WriteLine("  ----------------");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("  Database:   In-Memory");
        Console.WriteLine("  Logging:    Debug level (verbose)");
        Console.WriteLine("  JWT Secret: Auto-generated (unique per session)");
        Console.WriteLine("  Rate Limit: 1000 req/min (relaxed for testing)");
        Console.WriteLine();
        Console.ResetColor();
    }
}
