using Microsoft.Extensions.Logging;

namespace PrometheusServer.Logging;

/// <summary>
/// Helper class for logging server events.
/// </summary>
public static class ServerLogger
{
    /// <summary>
    /// Logs server startup information.
    /// </summary>
    public static void LogServerStartup(ILogger logger, string bindAddress, int port, int maxConnections)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Prometheus Chess Server");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("-----------------------");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"  Bind Address: {bindAddress}");
        Console.WriteLine($"  Port:         {port}");
        Console.WriteLine($"  Max Clients:  {maxConnections}");
        Console.WriteLine();
        Console.ResetColor();
    }

    /// <summary>
    /// Logs a match found event.
    /// </summary>
    public static void LogMatchFound(ILogger logger, string player1, int rating1, string player2, int rating2, string timeControl)
    {
        logger.LogInformation("Match found: {Player1} ({Rating1}) vs {Player2} ({Rating2}) - {TimeControl}",
            player1, rating1, player2, rating2, timeControl);
    }

    /// <summary>
    /// Logs a game start event.
    /// </summary>
    public static void LogGameStart(ILogger logger, string gameId, string whitePlayer, string blackPlayer)
    {
        logger.LogInformation("Game {GameId} started: {White} vs {Black}", gameId, whitePlayer, blackPlayer);
    }

    /// <summary>
    /// Logs a game end event.
    /// </summary>
    public static void LogGameEnd(ILogger logger, string gameId, string result, string reason)
    {
        logger.LogInformation("Game {GameId} ended: {Result} ({Reason})", gameId, result, reason);
    }

    /// <summary>
    /// Logs a move event.
    /// </summary>
    public static void LogMove(ILogger logger, string gameId, string player, string move, string fen)
    {
        logger.LogDebug("Move {Move} by {Player} in game {GameId}", move, player, gameId);
    }

    /// <summary>
    /// Logs a player connection event.
    /// </summary>
    public static void LogPlayerConnected(ILogger logger, string connectionId, string endpoint)
    {
        logger.LogInformation("New connection {ConnectionId} from {Endpoint}", connectionId, endpoint);
    }

    /// <summary>
    /// Logs a player disconnection event.
    /// </summary>
    public static void LogPlayerDisconnected(ILogger logger, string connectionId, string? username, string reason)
    {
        logger.LogInformation("Disconnected {Username} ({ConnectionId}): {Reason}", username ?? "Anonymous", connectionId, reason);
    }

    /// <summary>
    /// Logs a player login event.
    /// </summary>
    public static void LogPlayerLogin(ILogger logger, string username, int rating)
    {
        logger.LogInformation("Player logged in: {Username} (Rating: {Rating})", username, rating);
    }

    /// <summary>
    /// Logs a player registration event.
    /// </summary>
    public static void LogPlayerRegistered(ILogger logger, string username)
    {
        logger.LogInformation("New player registered: {Username}", username);
    }

    /// <summary>
    /// Logs a matchmaking queue event.
    /// </summary>
    public static void LogQueueJoin(ILogger logger, string username, int rating, string timeControl, int queueSize)
    {
        logger.LogInformation("Player {Username} ({Rating}) joined {TimeControl} queue. Queue size: {QueueSize}",
            username, rating, timeControl, queueSize);
    }

    /// <summary>
    /// Logs a rating change event.
    /// </summary>
    public static void LogRatingChange(ILogger logger, string username, int oldRating, int newRating)
    {
        var change = newRating - oldRating;
        logger.LogInformation("Rating change for {Username}: {OldRating} -> {NewRating} ({Change:+#;-#;0})",
            username, oldRating, newRating, change);
    }

    /// <summary>
    /// Logs an error event.
    /// </summary>
    public static void LogErrorEvent(ILogger logger, string context, string message, Exception? ex = null)
    {
        if (ex != null)
        {
            logger.LogError(ex, "{Context}: {Message}", context, message);
        }
        else
        {
            logger.LogError("{Context}: {Message}", context, message);
        }
    }

    /// <summary>
    /// Logs server statistics.
    /// </summary>
    public static void LogServerStats(ILogger logger, int connections, int activeGames, int playersInQueue, int totalPlayers)
    {
        logger.LogDebug("Stats - Connections: {Connections}, Games: {Games}, Queue: {Queue}, Players: {Players}",
            connections, activeGames, playersInQueue, totalPlayers);
    }

    /// <summary>
    /// Logs server shutdown.
    /// </summary>
    public static void LogServerShutdown(ILogger logger)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  Server shutting down...");
        Console.ResetColor();
        logger.LogInformation("Server shutting down...");
    }
}
