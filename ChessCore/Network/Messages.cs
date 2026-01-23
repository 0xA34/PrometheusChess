using System.Text.Json;
using System.Text.Json.Serialization;
using ChessCore.Models;

namespace ChessCore.Network;

/// <summary>
/// Types of messages that can be sent between client and server
/// </summary>
public enum MessageType
{
    // Connection & Authentication
    Connect = 0,
    ConnectResponse = 1,
    Disconnect = 2,
    Heartbeat = 3,
    HeartbeatAck = 4,

    // Authentication
    Login = 10,
    LoginResponse = 11,
    Logout = 12,
    Register = 13,
    RegisterResponse = 14,

    // Matchmaking
    FindMatch = 20,
    CancelFindMatch = 21,
    MatchFound = 22,
    QueueStatus = 23,

    // Game Flow
    GameStart = 30,
    GameState = 31,
    GameEnd = 32,

    // Moves
    MoveRequest = 40,
    MoveResponse = 41,
    MoveNotification = 42,

    // Game Actions
    Resign = 50,
    OfferDraw = 51,
    DrawOffered = 52,
    AcceptDraw = 53,
    DeclineDraw = 54,

    // Time
    TimeUpdate = 60,
    TimeoutWarning = 61,

    // Chat (optional)
    Chat = 70,

    // Errors
    Error = 99
}

/// <summary>
/// Base class for all network messages
/// </summary>
public abstract class NetworkMessage
{
    /// <summary>
    /// The type of this message
    /// </summary>
    [JsonPropertyName("type")]
    public abstract MessageType Type { get; }

    /// <summary>
    /// Server timestamp when the message was created/processed
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Unique message ID for tracking/acknowledgment
    /// </summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N")[..16];

    /// <summary>
    /// Serializes this message to JSON
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, GetType(), MessageSerializerContext.Default);
    }

    /// <summary>
    /// Deserializes a message from JSON
    /// </summary>
    public static NetworkMessage? FromJson(string json)
    {
        // First, parse to get the message type
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("type", out var typeElement))
            return null;

        var messageType = (MessageType)typeElement.GetInt32();

        return messageType switch
        {
            MessageType.Connect => JsonSerializer.Deserialize<ConnectMessage>(json, MessageSerializerContext.Default),
            MessageType.ConnectResponse => JsonSerializer.Deserialize<ConnectResponseMessage>(json, MessageSerializerContext.Default),
            MessageType.Heartbeat => JsonSerializer.Deserialize<HeartbeatMessage>(json, MessageSerializerContext.Default),
            MessageType.HeartbeatAck => JsonSerializer.Deserialize<HeartbeatAckMessage>(json, MessageSerializerContext.Default),
            MessageType.Login => JsonSerializer.Deserialize<LoginMessage>(json, MessageSerializerContext.Default),
            MessageType.LoginResponse => JsonSerializer.Deserialize<LoginResponseMessage>(json, MessageSerializerContext.Default),
            MessageType.Logout => JsonSerializer.Deserialize<LogoutMessage>(json, MessageSerializerContext.Default),
            MessageType.Register => JsonSerializer.Deserialize<RegisterMessage>(json, MessageSerializerContext.Default),
            MessageType.RegisterResponse => JsonSerializer.Deserialize<RegisterResponseMessage>(json, MessageSerializerContext.Default),
            MessageType.FindMatch => JsonSerializer.Deserialize<FindMatchMessage>(json, MessageSerializerContext.Default),
            MessageType.CancelFindMatch => JsonSerializer.Deserialize<CancelFindMatchMessage>(json, MessageSerializerContext.Default),
            MessageType.MatchFound => JsonSerializer.Deserialize<MatchFoundMessage>(json, MessageSerializerContext.Default),
            MessageType.QueueStatus => JsonSerializer.Deserialize<QueueStatusMessage>(json, MessageSerializerContext.Default),
            MessageType.GameStart => JsonSerializer.Deserialize<GameStartMessage>(json, MessageSerializerContext.Default),
            MessageType.GameState => JsonSerializer.Deserialize<GameStateMessage>(json, MessageSerializerContext.Default),
            MessageType.GameEnd => JsonSerializer.Deserialize<GameEndMessage>(json, MessageSerializerContext.Default),
            MessageType.MoveRequest => JsonSerializer.Deserialize<MoveRequestMessage>(json, MessageSerializerContext.Default),
            MessageType.MoveResponse => JsonSerializer.Deserialize<MoveResponseMessage>(json, MessageSerializerContext.Default),
            MessageType.MoveNotification => JsonSerializer.Deserialize<MoveNotificationMessage>(json, MessageSerializerContext.Default),
            MessageType.Resign => JsonSerializer.Deserialize<ResignMessage>(json, MessageSerializerContext.Default),
            MessageType.OfferDraw => JsonSerializer.Deserialize<OfferDrawMessage>(json, MessageSerializerContext.Default),
            MessageType.DrawOffered => JsonSerializer.Deserialize<DrawOfferedMessage>(json, MessageSerializerContext.Default),
            MessageType.AcceptDraw => JsonSerializer.Deserialize<AcceptDrawMessage>(json, MessageSerializerContext.Default),
            MessageType.DeclineDraw => JsonSerializer.Deserialize<DeclineDrawMessage>(json, MessageSerializerContext.Default),
            MessageType.TimeUpdate => JsonSerializer.Deserialize<TimeUpdateMessage>(json, MessageSerializerContext.Default),
            MessageType.Error => JsonSerializer.Deserialize<ErrorMessage>(json, MessageSerializerContext.Default),
            _ => null
        };
    }
}

#region Connection Messages

/// <summary>
/// Initial connection request from client
/// </summary>
public sealed class ConnectMessage : NetworkMessage
{
    public override MessageType Type => MessageType.Connect;

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = "1.0.0";

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = 1;
}

/// <summary>
/// Server response to connection request
/// </summary>
public sealed class ConnectResponseMessage : NetworkMessage
{
    public override MessageType Type => MessageType.ConnectResponse;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; set; } = "1.0.0";

    [JsonPropertyName("connectionId")]
    public string? ConnectionId { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    /// Whether the server is running in memory mode (data not persisted).
    /// </summary>
    [JsonPropertyName("isMemoryMode")]
    public bool IsMemoryMode { get; set; }
}

/// <summary>
/// Heartbeat message to keep connection alive
/// </summary>
public sealed class HeartbeatMessage : NetworkMessage
{
    public override MessageType Type => MessageType.Heartbeat;

    [JsonPropertyName("clientTime")]
    public long ClientTime { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>
/// Heartbeat acknowledgment from server
/// </summary>
public sealed class HeartbeatAckMessage : NetworkMessage
{
    public override MessageType Type => MessageType.HeartbeatAck;

    [JsonPropertyName("clientTime")]
    public long ClientTime { get; set; }

    [JsonPropertyName("serverTime")]
    public long ServerTime { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

#endregion

#region Authentication Messages

/// <summary>
/// Login request from client
/// </summary>
public sealed class LoginMessage : NetworkMessage
{
    public override MessageType Type => MessageType.Login;

    [JsonPropertyName("username")]
    public required string Username { get; set; }

    [JsonPropertyName("passwordHash")]
    public required string PasswordHash { get; set; }

    /// <summary>
    /// Optional token for session resumption
    /// </summary>
    [JsonPropertyName("sessionToken")]
    public string? SessionToken { get; set; }
}

/// <summary>
/// Server response to login request
/// </summary>
public sealed class LoginResponseMessage : NetworkMessage
{
    public override MessageType Type => MessageType.LoginResponse;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("playerId")]
    public string? PlayerId { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("sessionToken")]
    public string? SessionToken { get; set; }

    [JsonPropertyName("tokenExpiry")]
    public long? TokenExpiry { get; set; }

    [JsonPropertyName("rating")]
    public int Rating { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }
}

/// <summary>
/// Registration request from client
/// </summary>
public sealed class RegisterMessage : NetworkMessage
{
    public override MessageType Type => MessageType.Register;

    [JsonPropertyName("username")]
    public required string Username { get; set; }

    [JsonPropertyName("email")]
    public required string Email { get; set; }

    [JsonPropertyName("passwordHash")]
    public required string PasswordHash { get; set; }
}

/// <summary>
/// Server response to registration request
/// </summary>
public sealed class RegisterResponseMessage : NetworkMessage
{
    public override MessageType Type => MessageType.RegisterResponse;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("playerId")]
    public string? PlayerId { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }
}

/// <summary>
/// Client request to logout
/// </summary>
public sealed class LogoutMessage : NetworkMessage
{
    public override MessageType Type => MessageType.Logout;

    [JsonPropertyName("sessionToken")]
    public required string SessionToken { get; set; }
}

#endregion

#region Matchmaking Messages

/// <summary>
/// Request to find a match
/// </summary>
public sealed class FindMatchMessage : NetworkMessage
{
    public override MessageType Type => MessageType.FindMatch;

    [JsonPropertyName("sessionToken")]
    public required string SessionToken { get; set; }

    [JsonPropertyName("timeControl")]
    public TimeControlType TimeControl { get; set; } = TimeControlType.Rapid;

    [JsonPropertyName("initialTimeMs")]
    public int InitialTimeMs { get; set; } = 600000; // 10 minutes

    [JsonPropertyName("incrementMs")]
    public int IncrementMs { get; set; } = 0;

    [JsonPropertyName("ratingRange")]
    public int RatingRange { get; set; } = 200; // +/- rating tolerance
}

/// <summary>
/// Request to cancel matchmaking search
/// </summary>
public sealed class CancelFindMatchMessage : NetworkMessage
{
    public override MessageType Type => MessageType.CancelFindMatch;

    [JsonPropertyName("sessionToken")]
    public required string SessionToken { get; set; }
}

/// <summary>
/// Notification that a match has been found
/// </summary>
public sealed class MatchFoundMessage : NetworkMessage
{
    public override MessageType Type => MessageType.MatchFound;

    [JsonPropertyName("gameId")]
    public required string GameId { get; set; }

    [JsonPropertyName("yourColor")]
    public PieceColor YourColor { get; set; }

    [JsonPropertyName("opponentName")]
    public required string OpponentName { get; set; }

    [JsonPropertyName("opponentRating")]
    public int OpponentRating { get; set; }

    [JsonPropertyName("timeControl")]
    public TimeControlType TimeControl { get; set; }

    [JsonPropertyName("initialTimeMs")]
    public int InitialTimeMs { get; set; }

    [JsonPropertyName("incrementMs")]
    public int IncrementMs { get; set; }
}

/// <summary>
/// Queue status update
/// </summary>
public sealed class QueueStatusMessage : NetworkMessage
{
    public override MessageType Type => MessageType.QueueStatus;

    [JsonPropertyName("status")]
    public QueueStatus Status { get; set; }

    [JsonPropertyName("position")]
    public int? QueuePosition { get; set; }

    [JsonPropertyName("estimatedWaitSeconds")]
    public int? EstimatedWaitSeconds { get; set; }

    [JsonPropertyName("playersInQueue")]
    public int? PlayersInQueue { get; set; }
}

#endregion

#region Game Messages

/// <summary>
/// Notification that the game has started
/// </summary>
public sealed class GameStartMessage : NetworkMessage
{
    public override MessageType Type => MessageType.GameStart;

    [JsonPropertyName("gameId")]
    public required string GameId { get; set; }

    [JsonPropertyName("fen")]
    public string Fen { get; set; } = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    [JsonPropertyName("yourColor")]
    public PieceColor YourColor { get; set; }

    [JsonPropertyName("whitePlayer")]
    public required PlayerInfoDto WhitePlayer { get; set; }

    [JsonPropertyName("blackPlayer")]
    public required PlayerInfoDto BlackPlayer { get; set; }

    [JsonPropertyName("whiteTimeMs")]
    public int WhiteTimeMs { get; set; }

    [JsonPropertyName("blackTimeMs")]
    public int BlackTimeMs { get; set; }

    [JsonPropertyName("incrementMs")]
    public int IncrementMs { get; set; }
}

/// <summary>
/// Current game state (sent periodically or on request)
/// </summary>
public sealed class GameStateMessage : NetworkMessage
{
    public override MessageType Type => MessageType.GameState;

    [JsonPropertyName("gameId")]
    public required string GameId { get; set; }

    [JsonPropertyName("fen")]
    public required string Fen { get; set; }

    [JsonPropertyName("currentTurn")]
    public PieceColor CurrentTurn { get; set; }

    [JsonPropertyName("status")]
    public GameStatus Status { get; set; }

    [JsonPropertyName("whiteTimeMs")]
    public int WhiteTimeMs { get; set; }

    [JsonPropertyName("blackTimeMs")]
    public int BlackTimeMs { get; set; }

    [JsonPropertyName("moveHistory")]
    public List<string> MoveHistory { get; set; } = new();

    [JsonPropertyName("isCheck")]
    public bool IsCheck { get; set; }

    [JsonPropertyName("lastMove")]
    public string? LastMove { get; set; }

    [JsonPropertyName("moveSequence")]
    public int MoveSequence { get; set; }
}

/// <summary>
/// Notification that the game has ended
/// </summary>
public sealed class GameEndMessage : NetworkMessage
{
    public override MessageType Type => MessageType.GameEnd;

    [JsonPropertyName("gameId")]
    public required string GameId { get; set; }

    [JsonPropertyName("status")]
    public GameStatus Status { get; set; }

    [JsonPropertyName("reason")]
    public GameEndReason Reason { get; set; }

    [JsonPropertyName("winner")]
    public PieceColor? Winner { get; set; }

    [JsonPropertyName("finalFen")]
    public required string FinalFen { get; set; }

    [JsonPropertyName("ratingChange")]
    public int RatingChange { get; set; }

    [JsonPropertyName("newRating")]
    public int NewRating { get; set; }

    [JsonPropertyName("whiteRatingChange")]
    public int WhiteRatingChange { get; set; }

    [JsonPropertyName("blackRatingChange")]
    public int BlackRatingChange { get; set; }
}

#endregion

#region Move Messages

/// <summary>
/// Move request from client to server (client requests a move, server validates)
/// </summary>
public sealed class MoveRequestMessage : NetworkMessage
{
    public override MessageType Type => MessageType.MoveRequest;

    [JsonPropertyName("sessionToken")]
    public required string SessionToken { get; set; }

    [JsonPropertyName("gameId")]
    public required string GameId { get; set; }

    [JsonPropertyName("from")]
    public required string From { get; set; } // e.g., "e2"

    [JsonPropertyName("to")]
    public required string To { get; set; } // e.g., "e4"

    [JsonPropertyName("promotion")]
    public string? Promotion { get; set; } // "q", "r", "b", "n" for promotion

    [JsonPropertyName("clientTimestamp")]
    public long ClientTimestamp { get; set; }

    [JsonPropertyName("expectedSequence")]
    public int ExpectedSequence { get; set; }
}

/// <summary>
/// Server response to a move request
/// </summary>
public sealed class MoveResponseMessage : NetworkMessage
{
    public override MessageType Type => MessageType.MoveResponse;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("gameId")]
    public required string GameId { get; set; }

    [JsonPropertyName("move")]
    public string? Move { get; set; } // The validated move in coordinate notation

    [JsonPropertyName("newFen")]
    public string? NewFen { get; set; }

    [JsonPropertyName("validationResult")]
    public MoveValidationResult ValidationResult { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("isCheck")]
    public bool IsCheck { get; set; }

    [JsonPropertyName("isCheckmate")]
    public bool IsCheckmate { get; set; }

    [JsonPropertyName("isStalemate")]
    public bool IsStalemate { get; set; }

    [JsonPropertyName("whiteTimeMs")]
    public int WhiteTimeMs { get; set; }

    [JsonPropertyName("blackTimeMs")]
    public int BlackTimeMs { get; set; }

    [JsonPropertyName("moveSequence")]
    public int MoveSequence { get; set; }
}

/// <summary>
/// Notification sent to opponent when a move is made
/// </summary>
public sealed class MoveNotificationMessage : NetworkMessage
{
    public override MessageType Type => MessageType.MoveNotification;

    [JsonPropertyName("gameId")]
    public required string GameId { get; set; }

    [JsonPropertyName("move")]
    public required string Move { get; set; }

    [JsonPropertyName("newFen")]
    public required string NewFen { get; set; }

    [JsonPropertyName("isCheck")]
    public bool IsCheck { get; set; }

    [JsonPropertyName("isCheckmate")]
    public bool IsCheckmate { get; set; }

    [JsonPropertyName("isStalemate")]
    public bool IsStalemate { get; set; }

    [JsonPropertyName("whiteTimeMs")]
    public int WhiteTimeMs { get; set; }

    [JsonPropertyName("blackTimeMs")]
    public int BlackTimeMs { get; set; }

    [JsonPropertyName("moveSequence")]
    public int MoveSequence { get; set; }

    [JsonPropertyName("capturedPiece")]
    public string? CapturedPiece { get; set; }
}

#endregion

#region Game Action Messages

/// <summary>
/// Player resignation
/// </summary>
public sealed class ResignMessage : NetworkMessage
{
    public override MessageType Type => MessageType.Resign;

    [JsonPropertyName("sessionToken")]
    public required string SessionToken { get; set; }

    [JsonPropertyName("gameId")]
    public required string GameId { get; set; }
}

/// <summary>
/// Offer a draw
/// </summary>
public sealed class OfferDrawMessage : NetworkMessage
{
    public override MessageType Type => MessageType.OfferDraw;

    [JsonPropertyName("sessionToken")]
    public required string SessionToken { get; set; }

    [JsonPropertyName("gameId")]
    public required string GameId { get; set; }
}

/// <summary>
/// Notification that opponent offered a draw
/// </summary>
public sealed class DrawOfferedMessage : NetworkMessage
{
    public override MessageType Type => MessageType.DrawOffered;

    [JsonPropertyName("gameId")]
    public required string GameId { get; set; }

    [JsonPropertyName("offeredBy")]
    public required string OfferedBy { get; set; }
}

/// <summary>
/// Accept a draw offer
/// </summary>
public sealed class AcceptDrawMessage : NetworkMessage
{
    public override MessageType Type => MessageType.AcceptDraw;

    [JsonPropertyName("sessionToken")]
    public required string SessionToken { get; set; }

    [JsonPropertyName("gameId")]
    public required string GameId { get; set; }
}

/// <summary>
/// Decline a draw offer
/// </summary>
public sealed class DeclineDrawMessage : NetworkMessage
{
    public override MessageType Type => MessageType.DeclineDraw;

    [JsonPropertyName("sessionToken")]
    public required string SessionToken { get; set; }

    [JsonPropertyName("gameId")]
    public required string GameId { get; set; }
}

#endregion

#region Time Messages

/// <summary>
/// Time update sent periodically during a game
/// </summary>
public sealed class TimeUpdateMessage : NetworkMessage
{
    public override MessageType Type => MessageType.TimeUpdate;

    [JsonPropertyName("gameId")]
    public required string GameId { get; set; }

    [JsonPropertyName("whiteTimeMs")]
    public int WhiteTimeMs { get; set; }

    [JsonPropertyName("blackTimeMs")]
    public int BlackTimeMs { get; set; }

    [JsonPropertyName("currentTurn")]
    public PieceColor CurrentTurn { get; set; }
}

#endregion

#region Error Messages

/// <summary>
/// Error message from server
/// </summary>
public sealed class ErrorMessage : NetworkMessage
{
    public override MessageType Type => MessageType.Error;

    [JsonPropertyName("code")]
    public required string Code { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }

    [JsonPropertyName("details")]
    public string? Details { get; set; }

    [JsonPropertyName("relatedMessageId")]
    public string? RelatedMessageId { get; set; }
}

#endregion

#region DTOs

/// <summary>
/// Player information for transmission
/// </summary>
public sealed class PlayerInfoDto
{
    [JsonPropertyName("playerId")]
    public required string PlayerId { get; set; }

    [JsonPropertyName("username")]
    public required string Username { get; set; }

    [JsonPropertyName("rating")]
    public int Rating { get; set; }
}

#endregion

#region Serialization

/// <summary>
/// JSON serialization context for messages
/// </summary>
public static class MessageSerializerContext
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonSerializerOptions Options => Default;
}

#endregion
