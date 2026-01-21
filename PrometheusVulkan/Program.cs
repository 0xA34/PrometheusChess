using PrometheusVulkan.Core;

namespace PrometheusVulkan;

public static class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("Prometheus - Vulkan");
        Console.WriteLine();

        var options = ParseCommandLineArgs(args);

        try
        {
            using var app = new Application(options);
            app.Run();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Fatal error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();

            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey(true);

            Environment.Exit(1);
        }
    }

    private static ApplicationOptions ParseCommandLineArgs(string[] args)
    {
        var options = new ApplicationOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--server":
                case "-s":
                    if (i + 1 < args.Length)
                    {
                        options.ServerHost = args[++i];
                    }
                    break;

                case "--port":
                case "-p":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int port))
                    {
                        options.ServerPort = port;
                    }
                    break;

                case "--windowed":
                case "-w":
                    options.Fullscreen = false;
                    break;

                case "--fullscreen":
                case "-f":
                    options.Fullscreen = true;
                    break;

                case "--width":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int width))
                    {
                        options.WindowWidth = width;
                    }
                    break;

                case "--height":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int height))
                    {
                        options.WindowHeight = height;
                    }
                    break;

                case "--vsync":
                    options.VSync = true;
                    break;

                case "--no-vsync":
                    options.VSync = false;
                    break;

                case "--debug":
                case "-d":
                    options.EnableValidationLayers = true;
                    break;

                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;

                default:
                    Console.WriteLine($"Unknown argument: {args[i]}");
                    break;
            }
        }

        return options;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: Prometheus [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -s, --server <host>    Server hostname (default: 127.0.0.1)");
        Console.WriteLine("  -p, --port <port>      Server port (default: 8787)");
        Console.WriteLine("  -w, --windowed         Run in windowed mode");
        Console.WriteLine("  -f, --fullscreen       Run in fullscreen mode");
        Console.WriteLine("      --width <pixels>   Window width (default: 1280)");
        Console.WriteLine("      --height <pixels>  Window height (default: 720)");
        Console.WriteLine("      --vsync            Enable vertical sync");
        Console.WriteLine("      --no-vsync         Disable vertical sync");
        Console.WriteLine("  -d, --debug            Enable Vulkan validation layers");
        Console.WriteLine("  -h, --help             Show this help message");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  Prometheus --server chess.example.com --port 8787");
        Console.WriteLine("  Prometheus --windowed --width 1920 --height 1080");
        Console.WriteLine("  Prometheus --debug");
    }
}

public class ApplicationOptions
{
    public string ServerHost { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 8787;
    public bool Fullscreen { get; set; } = false;
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 720;
    public bool VSync { get; set; } = true;
    public bool EnableValidationLayers { get; set; } = false;
}
