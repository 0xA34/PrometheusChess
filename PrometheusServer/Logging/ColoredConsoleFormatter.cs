using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace PrometheusServer.Logging;

/// <summary>
/// Custom console formatter that provides colored output.
/// I don't know why I created this but yeah
/// As always, if you want to customise, go ahead.
/// </summary>
public sealed class ColoredConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "PrometheusColored";

    private readonly ColoredConsoleFormatterOptions _options;

    public ColoredConsoleFormatter(IOptionsMonitor<ColoredConsoleFormatterOptions> options)
        : base(FormatterName)
    {
        _options = options.CurrentValue;
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);

        if (message is null)
            return;

        // Get timestamp
        var timestamp = DateTime.Now.ToString(_options.TimestampFormat);

        // Get level info
        var (levelName, levelColor) = GetLevelInfo(logEntry.LogLevel);

        // Get category
        var category = GetShortCategory(logEntry.Category);

        // Write timestamp
        WriteColored(textWriter, $"[{timestamp}]", ConsoleColor.DarkGray);
        textWriter.Write(" ");

        // Write level with color
        WriteColored(textWriter, $"{levelName,-5}", levelColor);
        textWriter.Write(" ");

        // Write category
        WriteColored(textWriter, $"{category,-20}", ConsoleColor.Cyan);
        textWriter.Write(" ");

        // Write message
        WriteFormattedMessage(textWriter, message, logEntry.LogLevel);

        textWriter.WriteLine();

        // Write exception if present
        if (logEntry.Exception != null)
        {
            WriteException(textWriter, logEntry.Exception);
        }
    }

    private static (string Name, ConsoleColor Color) GetLevelInfo(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => ("TRACE", ConsoleColor.DarkGray),
            LogLevel.Debug => ("DEBUG", ConsoleColor.Gray),
            LogLevel.Information => ("INFO", ConsoleColor.Green),
            LogLevel.Warning => ("WARN", ConsoleColor.Yellow),
            LogLevel.Error => ("ERROR", ConsoleColor.Red),
            LogLevel.Critical => ("CRIT", ConsoleColor.DarkRed),
            _ => ("NONE", ConsoleColor.White)
        };
    }

    private static string GetShortCategory(string category)
    {
        var lastDot = category.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < category.Length - 1)
        {
            return category[(lastDot + 1)..];
        }
        return category;
    }

    private static void WriteFormattedMessage(TextWriter writer, string message, LogLevel level)
    {
        var messageColor = GetMessageColor(message, level);
        WriteColored(writer, message, messageColor);
    }

    private static ConsoleColor GetMessageColor(string message, LogLevel level)
    {
        if (level >= LogLevel.Warning)
        {
            return level == LogLevel.Warning ? ConsoleColor.Yellow : ConsoleColor.Red;
        }

        var lowerMessage = message.ToLowerInvariant();

        if (lowerMessage.Contains("success") || lowerMessage.Contains("started") ||
            lowerMessage.Contains("complete") || lowerMessage.Contains("authenticated"))
        {
            return ConsoleColor.Green;
        }

        if (lowerMessage.Contains("match found") || lowerMessage.Contains("game started") ||
            lowerMessage.Contains("game ended"))
        {
            return ConsoleColor.Cyan;
        }

        return ConsoleColor.White;
    }

    private static void WriteColored(TextWriter writer, string text, ConsoleColor color)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        writer.Write(text);
        Console.ForegroundColor = originalColor;
    }

    private static void WriteException(TextWriter writer, Exception exception)
    {
        WriteColored(writer, "         Exception: ", ConsoleColor.DarkRed);
        WriteColored(writer, exception.GetType().Name, ConsoleColor.Yellow);
        writer.WriteLine();

        WriteColored(writer, "         Message: ", ConsoleColor.DarkGray);
        WriteColored(writer, exception.Message, ConsoleColor.White);
        writer.WriteLine();

        if (!string.IsNullOrEmpty(exception.StackTrace))
        {
            WriteColored(writer, "         Stack Trace:", ConsoleColor.DarkGray);
            writer.WriteLine();

            var lines = exception.StackTrace.Split('\n').Take(5);
            foreach (var line in lines)
            {
                WriteColored(writer, "           ", ConsoleColor.DarkGray);
                WriteColored(writer, line.Trim(), ConsoleColor.DarkGray);
                writer.WriteLine();
            }
        }
    }
}

/// <summary>
/// Options for the colored console formatter.
/// </summary>
public sealed class ColoredConsoleFormatterOptions : ConsoleFormatterOptions
{
    public new string TimestampFormat { get; set; } = "HH:mm:ss.fff";
    public new bool UseUtcTimestamp { get; set; } = false;
}

/// <summary>
/// Extension methods for adding the colored console formatter.
/// </summary>
public static class ColoredConsoleLoggerExtensions
{
    public static ILoggingBuilder AddColoredConsole(this ILoggingBuilder builder)
    {
        return builder.AddColoredConsole(options => { });
    }

    public static ILoggingBuilder AddColoredConsole(
        this ILoggingBuilder builder,
        Action<ColoredConsoleFormatterOptions> configure)
    {
        builder.AddConsole(options =>
        {
            options.FormatterName = ColoredConsoleFormatter.FormatterName;
        });

        builder.AddConsoleFormatter<ColoredConsoleFormatter, ColoredConsoleFormatterOptions>(configure);

        return builder;
    }
}
