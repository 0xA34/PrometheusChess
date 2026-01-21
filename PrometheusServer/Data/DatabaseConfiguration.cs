namespace PrometheusServer.Data;

/// <summary>
/// Configuration settings for PostgreSQL database connection.
/// </summary>
public sealed class DatabaseConfiguration
{
    /// <summary>
    /// Database server hostname or IP address.
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// Database server port.
    /// </summary>
    public int Port { get; set; } = 5432;

    /// <summary>
    /// Name of the database.
    /// </summary>
    public string Database { get; set; } = "chess_game";

    /// <summary>
    /// Database username.
    /// </summary>
    public string Username { get; set; } = "chess_server";

    /// <summary>
    /// Database password. Should be loaded from environment variable in production.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// SSL mode for the connection (Disable, Prefer, Require, VerifyCA, VerifyFull).
    /// </summary>
    public string SslMode { get; set; } = "Prefer";

    /// <summary>
    /// Whether to use in-memory storage instead of PostgreSQL.
    /// Useful for development/testing without a database.
    /// </summary>
    public bool UseInMemory { get; set; } = false;

    /// <summary>
    /// Connection pool minimum size.
    /// </summary>
    public int MinPoolSize { get; set; } = 1;

    /// <summary>
    /// Connection pool maximum size.
    /// </summary>
    public int MaxPoolSize { get; set; } = 20;

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeout { get; set; } = 30;

    /// <summary>
    /// Command timeout in seconds.
    /// </summary>
    public int CommandTimeout { get; set; } = 30;

    /// <summary>
    /// Builds a Npgsql connection string from the configuration.
    /// </summary>
    /// <returns>PostgreSQL connection string</returns>
    public string BuildConnectionString()
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Database,
            Username = Username,
            Password = Password,
            SslMode = ParseSslMode(SslMode),
            MinPoolSize = MinPoolSize,
            MaxPoolSize = MaxPoolSize,
            Timeout = ConnectionTimeout,
            CommandTimeout = CommandTimeout,
            // Include error detail for better debugging
            IncludeErrorDetail = true
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// Parses the SSL mode string to Npgsql SslMode enum.
    /// </summary>
    private static Npgsql.SslMode ParseSslMode(string sslMode)
    {
        return sslMode.ToLowerInvariant() switch
        {
            "disable" => Npgsql.SslMode.Disable,
            "prefer" => Npgsql.SslMode.Prefer,
            "require" => Npgsql.SslMode.Require,
            "verifyca" => Npgsql.SslMode.VerifyCA,
            "verifyfull" => Npgsql.SslMode.VerifyFull,
            _ => Npgsql.SslMode.Prefer
        };
    }

    /// <summary>
    /// Validates the configuration.
    /// </summary>
    /// <returns>Validation result with any error messages</returns>
    public (bool IsValid, string? ErrorMessage) Validate()
    {
        if (UseInMemory)
        {
            // No validation needed for in-memory mode
            return (true, null);
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            return (false, "Database host is required");
        }

        if (Port <= 0 || Port > 65535)
        {
            return (false, "Database port must be between 1 and 65535");
        }

        if (string.IsNullOrWhiteSpace(Database))
        {
            return (false, "Database name is required");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            return (false, "Database username is required");
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            return (false, "Database password is required. Set it in appsettings.json or via CHESS_DB_PASSWORD environment variable");
        }

        return (true, null);
    }
}
