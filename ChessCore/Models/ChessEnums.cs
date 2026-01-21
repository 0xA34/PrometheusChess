namespace ChessCore.Models;

/// <summary>
/// Types of chess pieces
/// </summary>
public enum PieceType
{
    Pawn = 0,
    Knight = 1,
    Bishop = 2,
    Rook = 3,
    Queen = 4,
    King = 5
}

/// <summary>
/// Colors representing each player
/// </summary>
public enum PieceColor
{
    White = 0,
    Black = 1
}

/// <summary>
/// Current status of a game
/// </summary>
public enum GameStatus
{
    /// <summary>Waiting for opponent to join</summary>
    Waiting = 0,

    /// <summary>Game is currently being played</summary>
    InProgress = 1,

    /// <summary>White player won</summary>
    WhiteWon = 2,

    /// <summary>Black player won</summary>
    BlackWon = 3,

    /// <summary>Game ended in a draw</summary>
    Draw = 4,

    /// <summary>Game was aborted (player disconnected, etc.)</summary>
    Aborted = 5,

    /// <summary>Game was abandoned due to timeout</summary>
    Timeout = 6
}

/// <summary>
/// Reasons for game ending
/// </summary>
public enum GameEndReason
{
    Checkmate,
    Resignation,
    Timeout,
    Stalemate,
    InsufficientMaterial,
    FiftyMoveRule,
    ThreefoldRepetition,
    Agreement,
    DrawAgreement = Agreement,
    //Abandonment,
    Disconnection
}

/// <summary>
/// Result of a move validation
/// </summary>
public enum MoveValidationResult
{
    Valid,
    InvalidPiece,
    NotYourTurn,
    InvalidDestination,
    WouldBeInCheck,
    PathBlocked,
    InvalidCastling,
    InvalidEnPassant,
    InvalidPromotion,
    GameNotInProgress,
    PieceNotFound
}

/// <summary>
/// Special move types
/// </summary>
[Flags]
public enum SpecialMoveType
{
    None = 0,
    Capture = 1,
    EnPassant = 2,
    CastleKingside = 4,
    CastleQueenside = 8,
    PawnPromotion = 16,
    Check = 32,
    Checkmate = 64,
    DoublePawnPush = 128
}

/// <summary>
/// Time control types for matches
/// </summary>
public enum TimeControlType
{
    /// <summary>No time limit</summary>
    Unlimited,

    /// <summary>Bullet: 1-2 minutes</summary>
    Bullet,

    /// <summary>Blitz: 3-5 minutes</summary>
    Blitz,

    /// <summary>Rapid: 10-30 minutes</summary>
    Rapid,

    /// <summary>Classical: 30+ minutes</summary>
    Classical
}

/// <summary>
/// Player connection status
/// </summary>
public enum ConnectionStatus
{
    Connected,
    Disconnected,
    Reconnecting,
    TimedOut
}

/// <summary>
/// Queue status for matchmaking
/// </summary>
public enum QueueStatus
{
    NotInQueue,
    Searching,
    MatchFound,
    Cancelled,
    Error
}
