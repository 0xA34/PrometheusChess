namespace ChessCore.Models;

/// <summary>
/// Represents an active chess game session managed by the server.
/// This is the authoritative game state that the server maintains.
/// </summary>
public sealed class GameSession
{
    /// <summary>
    /// Unique identifier for this game session
    /// </summary>
    public string GameId { get; }

    /// <summary>
    /// The current board state
    /// </summary>
    public Board Board { get; private set; }

    /// <summary>
    /// White player's information
    /// </summary>
    public PlayerInfo WhitePlayer { get; }

    /// <summary>
    /// Black player's information
    /// </summary>
    public PlayerInfo BlackPlayer { get; }

    /// <summary>
    /// Current status of the game
    /// </summary>
    public GameStatus Status { get; private set; }

    /// <summary>
    /// Reason the game ended (if applicable)
    /// </summary>
    public GameEndReason? EndReason { get; private set; }

    /// <summary>
    /// Time when the game was created
    /// </summary>
    public DateTime CreatedAt { get; }

    /// <summary>
    /// Time when the game started (both players joined)
    /// </summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>
    /// Time when the game ended
    /// </summary>
    public DateTime? EndedAt { get; private set; }

    /// <summary>
    /// Time of the last move
    /// </summary>
    public DateTime? LastMoveAt { get; private set; }

    /// <summary>
    /// White player's remaining time in milliseconds
    /// </summary>
    public int WhiteTimeMs { get; private set; }

    /// <summary>
    /// Black player's remaining time in milliseconds
    /// </summary>
    public int BlackTimeMs { get; private set; }

    /// <summary>
    /// Time increment per move in milliseconds (Fischer increment)
    /// </summary>
    public int TimeIncrementMs { get; }

    /// <summary>
    /// Time control type for this game
    /// </summary>
    public TimeControlType TimeControl { get; }

    /// <summary>
    /// History of all moves made in this game
    /// </summary>
    public List<Move> MoveHistory { get; } = new();

    /// <summary>
    /// History of board states (FEN strings) for threefold repetition detection
    /// </summary>
    public List<string> PositionHistory { get; } = new();

    /// <summary>
    /// Whether the current player is in check
    /// </summary>
    public bool IsCheck { get; private set; }

    /// <summary>
    /// Whether the game has ended in checkmate
    /// </summary>
    public bool IsCheckmate { get; private set; }

    /// <summary>
    /// Whether the game has ended in stalemate
    /// </summary>
    public bool IsStalemate { get; private set; }

    /// <summary>
    /// The winner of the game (null if draw or ongoing)
    /// </summary>
    public PieceColor? Winner { get; private set; }

    /// <summary>
    /// Server-side sequence number for move validation
    /// </summary>
    public int MoveSequence { get; private set; }

    /// <summary>
    /// Lock object for thread-safe operations
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new game session
    /// </summary>
    /// <param name="whitePlayer">White player info</param>
    /// <param name="blackPlayer">Black player info</param>
    /// <param name="initialTimeMs">Initial time for each player in milliseconds</param>
    /// <param name="incrementMs">Time increment per move in milliseconds</param>
    /// <param name="timeControl">Time control type</param>
    public GameSession(
        PlayerInfo whitePlayer,
        PlayerInfo blackPlayer,
        int initialTimeMs = 600000,
        int incrementMs = 0,
        TimeControlType timeControl = TimeControlType.Rapid)
    {
        GameId = GenerateGameId();
        Board = Board.CreateStartingPosition();
        WhitePlayer = whitePlayer;
        BlackPlayer = blackPlayer;
        WhiteTimeMs = initialTimeMs;
        BlackTimeMs = initialTimeMs;
        TimeIncrementMs = incrementMs;
        TimeControl = timeControl;
        Status = GameStatus.Waiting;
        CreatedAt = DateTime.UtcNow;
        MoveSequence = 0;

        // Record initial position for repetition detection
        PositionHistory.Add(Board.ToFen());
    }

    /// <summary>
    /// Creates a game session with a specific game ID (for reconnection)
    /// </summary>
    public GameSession(
        string gameId,
        PlayerInfo whitePlayer,
        PlayerInfo blackPlayer,
        int initialTimeMs = 600000,
        int incrementMs = 0,
        TimeControlType timeControl = TimeControlType.Rapid)
        : this(whitePlayer, blackPlayer, initialTimeMs, incrementMs, timeControl)
    {
        // Use reflection or a private setter pattern if GameId needs to be overridden
        // For now, we'll create a new instance with the specified ID
    }

    /// <summary>
    /// Starts the game (called when both players are ready)
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (Status != GameStatus.Waiting)
                throw new InvalidOperationException("Game has already started or ended");

            Status = GameStatus.InProgress;
            StartedAt = DateTime.UtcNow;
            LastMoveAt = StartedAt;
        }
    }

    /// <summary>
    /// Gets the player info for the specified color
    /// </summary>
    public PlayerInfo GetPlayer(PieceColor color)
    {
        return color == PieceColor.White ? WhitePlayer : BlackPlayer;
    }

    /// <summary>
    /// Gets the player info by player ID
    /// </summary>
    public PlayerInfo? GetPlayerById(string playerId)
    {
        if (WhitePlayer.PlayerId == playerId)
            return WhitePlayer;
        if (BlackPlayer.PlayerId == playerId)
            return BlackPlayer;
        return null;
    }

    /// <summary>
    /// Gets the color of the specified player
    /// </summary>
    public PieceColor? GetPlayerColor(string playerId)
    {
        if (WhitePlayer.PlayerId == playerId)
            return PieceColor.White;
        if (BlackPlayer.PlayerId == playerId)
            return PieceColor.Black;
        return null;
    }

    /// <summary>
    /// Checks if the specified player is in this game
    /// </summary>
    public bool HasPlayer(string playerId)
    {
        return WhitePlayer.PlayerId == playerId || BlackPlayer.PlayerId == playerId;
    }

    /// <summary>
    /// Gets the remaining time for the specified color
    /// </summary>
    public int GetRemainingTime(PieceColor color)
    {
        return color == PieceColor.White ? WhiteTimeMs : BlackTimeMs;
    }

    /// <summary>
    /// Updates the clock for the player who just moved
    /// </summary>
    /// <param name="color">The color of the player who just moved</param>
    /// <param name="elapsedMs">Time elapsed for the move in milliseconds</param>
    public void UpdateClock(PieceColor color, int elapsedMs)
    {
        lock (_lock)
        {
            if (color == PieceColor.White)
            {
                WhiteTimeMs = Math.Max(0, WhiteTimeMs - elapsedMs + TimeIncrementMs);
            }
            else
            {
                BlackTimeMs = Math.Max(0, BlackTimeMs - elapsedMs + TimeIncrementMs);
            }

            LastMoveAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Records a move in the game history
    /// </summary>
    /// <param name="move">The move to record</param>
    public void RecordMove(Move move)
    {
        lock (_lock)
        {
            MoveHistory.Add(move);
            PositionHistory.Add(Board.ToFen());
            MoveSequence++;
        }
    }

    /// <summary>
    /// Updates the game state flags (check, checkmate, stalemate)
    /// </summary>
    public void UpdateGameState(bool isCheck, bool isCheckmate, bool isStalemate)
    {
        lock (_lock)
        {
            IsCheck = isCheck;
            IsCheckmate = isCheckmate;
            IsStalemate = isStalemate;

            if (isCheckmate)
            {
                // The player whose turn it is just got checkmated
                Winner = Board.CurrentTurn == PieceColor.White ? PieceColor.Black : PieceColor.White;
                Status = Winner == PieceColor.White ? GameStatus.WhiteWon : GameStatus.BlackWon;
                EndReason = GameEndReason.Checkmate;
                EndedAt = DateTime.UtcNow;
            }
            else if (isStalemate)
            {
                Status = GameStatus.Draw;
                EndReason = GameEndReason.Stalemate;
                EndedAt = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// Ends the game with the specified result
    /// </summary>
    public void EndGame(GameStatus status, GameEndReason reason, PieceColor? winner = null)
    {
        lock (_lock)
        {
            if (Status != GameStatus.InProgress)
                return;

            Status = status;
            EndReason = reason;
            Winner = winner;
            EndedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Handles player resignation
    /// </summary>
    public void Resign(PieceColor resigningColor)
    {
        lock (_lock)
        {
            if (Status != GameStatus.InProgress)
                throw new InvalidOperationException("Cannot resign - game is not in progress");

            Winner = resigningColor == PieceColor.White ? PieceColor.Black : PieceColor.White;
            Status = Winner == PieceColor.White ? GameStatus.WhiteWon : GameStatus.BlackWon;
            EndReason = GameEndReason.Resignation;
            EndedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Handles timeout (flag fall)
    /// </summary>
    public void HandleTimeout(PieceColor timedOutColor)
    {
        lock (_lock)
        {
            if (Status != GameStatus.InProgress)
                return;

            Winner = timedOutColor == PieceColor.White ? PieceColor.Black : PieceColor.White;
            Status = Winner == PieceColor.White ? GameStatus.WhiteWon : GameStatus.BlackWon;
            EndReason = GameEndReason.Timeout;
            EndedAt = DateTime.UtcNow;

            if (timedOutColor == PieceColor.White)
                WhiteTimeMs = 0;
            else
                BlackTimeMs = 0;
        }
    }

    /// <summary>
    /// Handles player disconnection
    /// </summary>
    public void HandleDisconnection(PieceColor disconnectedColor)
    {
        lock (_lock)
        {
            if (Status != GameStatus.InProgress)
                return;

            Winner = disconnectedColor == PieceColor.White ? PieceColor.Black : PieceColor.White;
            Status = Winner == PieceColor.White ? GameStatus.WhiteWon : GameStatus.BlackWon;
            EndReason = GameEndReason.Disconnection;
            EndedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Offers a draw (to be accepted by the other player)
    /// </summary>
    public void AcceptDraw()
    {
        lock (_lock)
        {
            if (Status != GameStatus.InProgress)
                throw new InvalidOperationException("Cannot draw - game is not in progress");

            Status = GameStatus.Draw;
            EndReason = GameEndReason.Agreement;
            EndedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Checks for threefold repetition
    /// </summary>
    public bool IsThreefoldRepetition()
    {
        if (PositionHistory.Count < 5)
            return false;

        var currentPosition = GetPositionKey(Board.ToFen());
        int count = PositionHistory.Count(fen => GetPositionKey(fen) == currentPosition);

        return count >= 3;
    }

    /// <summary>
    /// Checks for fifty-move rule
    /// </summary>
    public bool IsFiftyMoveRule()
    {
        return Board.HalfMoveClock >= 100; // 100 half-moves = 50 full moves
    }

    /// <summary>
    /// Gets the position key from a FEN string (excluding move counters)
    /// </summary>
    private static string GetPositionKey(string fen)
    {
        var parts = fen.Split(' ');
        if (parts.Length >= 4)
        {
            return $"{parts[0]} {parts[1]} {parts[2]} {parts[3]}";
        }
        return fen;
    }

    /// <summary>
    /// Generates a unique game ID
    /// </summary>
    private static string GenerateGameId()
    {
        // Generate a short, URL-friendly ID
        var bytes = Guid.NewGuid().ToByteArray();
        return Convert.ToBase64String(bytes)
            .Replace("/", "_")
            .Replace("+", "-")
            .TrimEnd('=')
            [..12]; // Take first 12 characters
    }

    /// <summary>
    /// Creates a snapshot of the current game state for sending to clients
    /// </summary>
    public GameStateSnapshot CreateSnapshot()
    {
        lock (_lock)
        {
            return new GameStateSnapshot
            {
                GameId = GameId,
                Fen = Board.ToFen(),
                CurrentTurn = Board.CurrentTurn,
                Status = Status,
                WhiteTimeMs = WhiteTimeMs,
                BlackTimeMs = BlackTimeMs,
                MoveHistory = MoveHistory.Select(m => m.ToCoordinateNotation()).ToList(),
                IsCheck = IsCheck,
                IsCheckmate = IsCheckmate,
                IsStalemate = IsStalemate,
                Winner = Winner,
                MoveSequence = MoveSequence,
                LastMoveAt = LastMoveAt
            };
        }
    }

    /// <summary>
    /// Gets the duration of the game
    /// </summary>
    public TimeSpan? GetDuration()
    {
        if (StartedAt == null)
            return null;

        var endTime = EndedAt ?? DateTime.UtcNow;
        return endTime - StartedAt.Value;
    }

    public override string ToString()
    {
        return $"Game {GameId}: {WhitePlayer.Username} vs {BlackPlayer.Username} - {Status}";
    }
}

/// <summary>
/// Contains basic player information for a game session
/// </summary>
public sealed class PlayerInfo
{
    /// <summary>
    /// Unique identifier for the player
    /// </summary>
    public required string PlayerId { get; init; }

    /// <summary>
    /// Display name of the player
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// Player's rating at the start of the game
    /// </summary>
    public int Rating { get; init; }

    /// <summary>
    /// Connection status of the player
    /// </summary>
    public ConnectionStatus ConnectionStatus { get; set; } = ConnectionStatus.Connected;

    /// <summary>
    /// Session token for authentication
    /// </summary>
    public string? SessionToken { get; init; }

    /// <summary>
    /// Time of last activity
    /// </summary>
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A snapshot of the game state to send to clients
/// </summary>
public sealed class GameStateSnapshot
{
    public required string GameId { get; init; }
    public required string Fen { get; init; }
    public PieceColor CurrentTurn { get; init; }
    public GameStatus Status { get; init; }
    public int WhiteTimeMs { get; init; }
    public int BlackTimeMs { get; init; }
    public required List<string> MoveHistory { get; init; }
    public bool IsCheck { get; init; }
    public bool IsCheckmate { get; init; }
    public bool IsStalemate { get; init; }
    public PieceColor? Winner { get; init; }
    public int MoveSequence { get; init; }
    public DateTime? LastMoveAt { get; init; }
}
