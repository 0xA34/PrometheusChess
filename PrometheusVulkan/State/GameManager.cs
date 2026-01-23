using ChessCore.Models;
using PrometheusVulkan.Network;

namespace PrometheusVulkan.State;

/// <summary>
/// Game states representing the current state of the client.
/// </summary>
public enum GameState
{
    Disconnected,
    Connecting,
    Connected,
    LoggingIn,
    LoggedIn,
    InLobby,
    Matchmaking,
    InGame,
    GameOver
}

/// <summary>
/// Manages global game state, player information, and coordinates between UI and network.
/// </summary>
public sealed class GameManager : IDisposable
{
    #region Events

    public event Action<GameState>? StateChanged;
    public event Action? PlayerInfoUpdated;
    public event Action<string, string>? ErrorOccurred;
    public event Action<string>? StatusMessage;
    public event Action? GameBoardUpdated;
    public event Action<string>? DrawOffered;
    public event Action<GameEndInfo>? GameEnded;
    public event Action<int, int>? TimeUpdated;
    public event Action<string>? ConnectionRetrying;

    #endregion

    #region Properties

    public GameState CurrentState { get; private set; } = GameState.Disconnected;
    public GameClient Client { get; }

    // Connection settings
    public string ServerHost { get; set; }
    public int ServerPort { get; set; }

    // Player info
    public string? PlayerId { get; private set; }
    public string? Username { get; private set; }
    public int Rating { get; private set; }
    public string? SessionToken { get; private set; }

    // Current game info
    public string? CurrentGameId { get; private set; }
    public PieceColor PlayerColor { get; private set; }
    public string? OpponentName { get; private set; }
    public int OpponentRating { get; private set; }
    public string? CurrentFen { get; private set; }
    public PieceColor CurrentTurn { get; private set; }
    public int WhiteTimeMs { get; private set; }
    public int BlackTimeMs { get; private set; }
    public bool IsInCheck { get; private set; }
    public Board? CurrentBoard { get; private set; }

    // Matchmaking
    public int QueuePosition { get; private set; }
    public int EstimatedWaitSeconds { get; private set; }

    // Auto return to lobby settings
    public float ReturnToLobbyDelaySeconds { get; set; } = 5.0f;
    public bool AutoReturnToLobbyEnabled { get; set; } = true;

    // Last game result info
    public GameEndInfo? LastGameResult { get; private set; }

    // Server memory mode indicator
    public bool IsServerInMemoryMode => Client.IsServerInMemoryMode;

    // History
    public List<Move> MoveHistory { get; private set; } = new();

    #endregion

    #region Private Fields

    private bool _isDisposed;
    private float _returnToLobbyTimer;
    private bool _isWaitingToReturnToLobby;
    private CancellationTokenSource? _connectionCts;
    private Move? _lastSentMove;

    #endregion

    public GameManager(string serverHost = "127.0.0.1", int serverPort = 8787)
    {
        ServerHost = serverHost;
        ServerPort = serverPort;

        Client = new GameClient
        {
            MaxRetryAttempts = 5,
            RetryDelayMs = 2000,
            AutoRetryOnDisconnect = false
        };

        ConnectClientEvents();

        Console.WriteLine("[GameManager] Initialized");
    }

    #region Client Event Connections

    private void ConnectClientEvents()
    {
        Client.Connected += OnClientConnected;
        Client.Disconnected += OnClientDisconnected;
        Client.ConnectionFailed += OnConnectionFailed;
        Client.ConnectionRetrying += OnConnectionRetrying;
        Client.LoginSuccess += OnLoginSuccess;
        Client.LoginFailed += OnLoginFailed;
        Client.RegisterSuccess += OnRegisterSuccess;
        Client.RegisterFailed += OnRegisterFailed;
        Client.MatchFound += OnMatchFound;
        Client.QueueStatusUpdated += OnQueueStatusUpdated;
        Client.GameStarted += OnGameStarted;
        Client.GameStateUpdated += OnGameStateUpdated;
        Client.MoveResponseReceived += OnMoveResponseReceived;
        Client.OpponentMoved += OnOpponentMoved;
        Client.GameEnded += OnGameEnded;
        Client.DrawOffered += OnDrawOffered;
        Client.TimeUpdated += OnTimeUpdated;
        Client.ErrorReceived += OnErrorReceived;
    }

    private void DisconnectClientEvents()
    {
        Client.Connected -= OnClientConnected;
        Client.Disconnected -= OnClientDisconnected;
        Client.ConnectionFailed -= OnConnectionFailed;
        Client.ConnectionRetrying -= OnConnectionRetrying;
        Client.LoginSuccess -= OnLoginSuccess;
        Client.LoginFailed -= OnLoginFailed;
        Client.RegisterSuccess -= OnRegisterSuccess;
        Client.RegisterFailed -= OnRegisterFailed;
        Client.MatchFound -= OnMatchFound;
        Client.QueueStatusUpdated -= OnQueueStatusUpdated;
        Client.GameStarted -= OnGameStarted;
        Client.GameStateUpdated -= OnGameStateUpdated;
        Client.MoveResponseReceived -= OnMoveResponseReceived;
        Client.OpponentMoved -= OnOpponentMoved;
        Client.GameEnded -= OnGameEnded;
        Client.DrawOffered -= OnDrawOffered;
        Client.TimeUpdated -= OnTimeUpdated;
        Client.ErrorReceived -= OnErrorReceived;
    }

    #endregion

    #region Public API - Connection

    public async Task ConnectToServerAsync()
    {
        if (CurrentState != GameState.Disconnected)
        {
            Console.WriteLine("[GameManager] Already connected or connecting");
            return;
        }

        SetState(GameState.Connecting);
        StatusMessage?.Invoke("Connecting to server...");

        _connectionCts = new CancellationTokenSource();

        try
        {
            await Client.ConnectAsync(ServerHost, ServerPort, _connectionCts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage?.Invoke("Connection cancelled");
            SetState(GameState.Disconnected);
        }
    }

    public async Task ConnectToServerAsync(string host, int port)
    {
        ServerHost = host;
        ServerPort = port;
        await ConnectToServerAsync();
    }

    public void CancelConnection()
    {
        _connectionCts?.Cancel();
    }

    public void DisconnectFromServer()
    {
        _connectionCts?.Cancel();
        Client.Disconnect();
        ResetGameState();
        ResetPlayerInfo();
        SetState(GameState.Disconnected);
        StatusMessage?.Invoke("Disconnected from server");
    }

    #endregion

    #region Public API - Authentication

    public async Task RegisterAsync(string username, string email, string password)
    {
        if (CurrentState != GameState.Connected)
        {
            ErrorOccurred?.Invoke("Error", "Not connected to server");
            return;
        }

        StatusMessage?.Invoke("Registering account...");
        await Client.RegisterAsync(username, email, password);
    }

    public async Task LoginAsync(string username, string password)
    {
        if (CurrentState != GameState.Connected)
        {
            ErrorOccurred?.Invoke("Error", "Not connected to server");
            return;
        }

        SetState(GameState.LoggingIn);
        StatusMessage?.Invoke("Logging in...");
        await Client.LoginAsync(username, password);
    }

    public async Task LoginWithTokenAsync(string token)
    {
        if (CurrentState != GameState.Connected)
        {
            ErrorOccurred?.Invoke("Error", "Not connected to server");
            return;
        }

        SetState(GameState.LoggingIn);
        StatusMessage?.Invoke("Logging in with session...");
        await Client.LoginWithTokenAsync(token);
    }

    public async Task LogoutAsync()
    {
        await Client.LogoutAsync();
        SessionToken = null;
        PlayerId = null;
        Username = null;
        Rating = 0;
        ResetGameState();
        SetState(GameState.Connected);
        PlayerInfoUpdated?.Invoke();
        StatusMessage?.Invoke("Logged out");
    }

    #endregion

    #region Public API - Matchmaking

    public async Task FindMatchAsync(TimeControlType timeControl = TimeControlType.Rapid,
        int initialTimeMs = 600000, int incrementMs = 0, int ratingRange = 200)
    {
        if (CurrentState != GameState.InLobby)
        {
            ErrorOccurred?.Invoke("Error", "Not in lobby");
            return;
        }

        SetState(GameState.Matchmaking);
        StatusMessage?.Invoke("Searching for opponent...");
        await Client.FindMatchAsync(timeControl, initialTimeMs, incrementMs, ratingRange);
    }

    public async Task CancelMatchmakingAsync()
    {
        if (CurrentState != GameState.Matchmaking)
            return;

        await Client.CancelFindMatchAsync();
        SetState(GameState.InLobby);
        StatusMessage?.Invoke("Search cancelled");
    }

    #endregion

    #region Public API - Game Actions

    public async Task SendMoveAsync(string from, string to, PieceType? promotion = null)
    {
        if (CurrentState != GameState.InGame)
        {
            Console.WriteLine("[GameManager] Cannot send move: not in game");
            return;
        }

        if (!IsMyTurn())
        {
            Console.WriteLine("[GameManager] Cannot send move: not your turn");
            ErrorOccurred?.Invoke("Invalid Action", "It's not your turn");
            return;
        }

        try
        {
            var fromPos = Position.FromAlgebraic(from);
            var toPos = Position.FromAlgebraic(to);
            var piece = CurrentBoard?.GetPieceAt(fromPos);
            var pieceType = piece?.Type ?? PieceType.Pawn;

            _lastSentMove = new Move(fromPos, toPos, pieceType, PlayerColor);

            if (promotion.HasValue)
            {
                _lastSentMove = _lastSentMove with { PromotionType = promotion, SpecialMoveFlags = SpecialMoveType.PawnPromotion };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameManager] Error preparing move history: {ex.Message}");
            _lastSentMove = null;
        }

        await Client.SendMoveAsync(from, to, promotion);
    }

    public async Task ResignAsync()
    {
        if (CurrentState != GameState.InGame)
            return;

        StatusMessage?.Invoke("Resigning...");
        await Client.ResignAsync();
    }

    public async Task OfferDrawAsync()
    {
        if (CurrentState != GameState.InGame)
            return;

        StatusMessage?.Invoke("Offering draw...");
        await Client.OfferDrawAsync();
    }

    public async Task AcceptDrawAsync()
    {
        if (CurrentState != GameState.InGame)
            return;

        StatusMessage?.Invoke("Accepting draw...");
        await Client.AcceptDrawAsync();
    }

    public async Task DeclineDrawAsync()
    {
        if (CurrentState != GameState.InGame)
            return;

        StatusMessage?.Invoke("Draw declined");
        await Client.DeclineDrawAsync();
    }

    #endregion

    #region Client Event Handlers

    private void OnClientConnected()
    {
        Console.WriteLine("[GameManager] Connected to server");
        SetState(GameState.Connected);
        StatusMessage?.Invoke("Connected to server!");
    }

    private void OnClientDisconnected(string reason)
    {
        Console.WriteLine($"[GameManager] Disconnected: {reason}");
        ResetPlayerInfo();
        ResetGameState();
        SetState(GameState.Disconnected);
        ErrorOccurred?.Invoke("Disconnected", reason);
    }

    private void OnConnectionFailed(string error)
    {
        Console.WriteLine($"[GameManager] Connection failed: {error}");
        SetState(GameState.Disconnected);
        ErrorOccurred?.Invoke("Connection Failed", error);
    }

    private void OnConnectionRetrying(string message)
    {
        Console.WriteLine($"[GameManager] {message}");
        StatusMessage?.Invoke(message);
        ConnectionRetrying?.Invoke(message);
    }

    private void OnLoginSuccess(string playerId, string username, int rating, string sessionToken)
    {
        PlayerId = playerId;
        Username = username;
        Rating = rating;
        SessionToken = sessionToken;

        Console.WriteLine($"[GameManager] Logged in as {username} (Rating: {rating})");
        SetState(GameState.InLobby);
        PlayerInfoUpdated?.Invoke();
        StatusMessage?.Invoke($"Welcome back, {username}!");
    }

    private void OnLoginFailed(string message)
    {
        Console.WriteLine($"[GameManager] Login failed: {message}");
        SetState(GameState.Connected);
        ErrorOccurred?.Invoke("Login Failed", message);
    }

    private void OnRegisterSuccess(string playerId)
    {
        Console.WriteLine($"[GameManager] Registration successful: {playerId}");
        StatusMessage?.Invoke("Registration successful! Please login.");
    }

    private void OnRegisterFailed(string message)
    {
        Console.WriteLine($"[GameManager] Registration failed: {message}");
        ErrorOccurred?.Invoke("Registration Failed", message);
    }

    private void OnMatchFound(string gameId, int yourColor, string opponentName, int opponentRating)
    {
        CurrentGameId = gameId;
        PlayerColor = (PieceColor)yourColor;
        OpponentName = opponentName;
        OpponentRating = opponentRating;

        Console.WriteLine($"[GameManager] Match found! vs {opponentName} ({opponentRating})");
        StatusMessage?.Invoke($"Match found! Playing against {opponentName} ({opponentRating})");
    }

    private void OnQueueStatusUpdated(int status, int position, int estimatedWait)
    {
        QueuePosition = position;
        EstimatedWaitSeconds = estimatedWait;
        Console.WriteLine($"[GameManager] Queue status: position {position}, estimated wait {estimatedWait}s");
    }

    private void OnGameStarted(string gameId, string fen, int yourColor,
        string whiteName, int whiteRating, string blackName, int blackRating)
    {
        CurrentGameId = gameId;
        CurrentFen = fen;
        PlayerColor = (PieceColor)yourColor;
        CurrentTurn = ParseTurnFromFen(fen);
        CurrentBoard = Board.FromFen(fen);
        MoveHistory.Clear();

        // Clear any pending return to lobby
        _isWaitingToReturnToLobby = false;
        _returnToLobbyTimer = 0;
        LastGameResult = null;

        if (PlayerColor == PieceColor.White)
        {
            OpponentName = blackName;
            OpponentRating = blackRating;
        }
        else
        {
            OpponentName = whiteName;
            OpponentRating = whiteRating;
        }

        Console.WriteLine($"[GameManager] Game started! Playing as {PlayerColor}");
        SetState(GameState.InGame);
        GameBoardUpdated?.Invoke();
        StatusMessage?.Invoke($"Game started! You are {PlayerColor}");
    }

    private void OnGameStateUpdated(string gameId, string fen, int currentTurn, int status,
        int whiteTimeMs, int blackTimeMs, bool isCheck)
    {
        if (gameId != CurrentGameId) return;

        CurrentFen = fen;
        CurrentTurn = (PieceColor)currentTurn;
        WhiteTimeMs = whiteTimeMs;
        BlackTimeMs = blackTimeMs;
        IsInCheck = isCheck;
        CurrentBoard = Board.FromFen(fen);

        GameBoardUpdated?.Invoke();
        TimeUpdated?.Invoke(whiteTimeMs, blackTimeMs);
    }

    private void OnMoveResponseReceived(bool success, string message, string newFen,
        bool isCheck, bool isCheckmate, bool isStalemate)
    {
        if (success)
        {
            if (_lastSentMove != null)
            {
                MoveHistory.Add(_lastSentMove);
                _lastSentMove = null;
            }

            CurrentFen = newFen;
            IsInCheck = isCheck;
            CurrentTurn = ParseTurnFromFen(newFen);
            CurrentBoard = Board.FromFen(newFen);

            GameBoardUpdated?.Invoke();

            if (isCheckmate)
            {
                StatusMessage?.Invoke("Checkmate!");
                SetState(GameState.GameOver);
            }
            else if (isStalemate)
            {
                StatusMessage?.Invoke("Stalemate!");
                SetState(GameState.GameOver);
            }
            else if (isCheck)
            {
                StatusMessage?.Invoke("Check!");
            }
        }
        else
        {
            Console.WriteLine($"[GameManager] Move rejected: {message}");
            ErrorOccurred?.Invoke("Invalid Move", message);
        }
    }

    private void OnOpponentMoved(string move, string newFen, bool isCheck,
        bool isCheckmate, bool isStalemate, int whiteTimeMs, int blackTimeMs)
    {
        try
        {
            if (CurrentBoard != null)
            {
                var opponentColor = PlayerColor == PieceColor.White ? PieceColor.Black : PieceColor.White;
                // Parse opponent move before updating board
                // Basic parsing to extract piece type from current board state
                if (move.Length >= 4)
                {
                    var fromStr = move.Substring(0, 2);
                    var fromPos = Position.FromAlgebraic(fromStr);
                    var piece = CurrentBoard.GetPieceAt(fromPos);
                    var pieceType = piece?.Type ?? PieceType.Pawn;
                    var parsedMove = Move.FromCoordinateNotation(move, pieceType, opponentColor);
                    MoveHistory.Add(parsedMove);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameManager] Error parsing opponent move: {ex.Message}");
        }

        CurrentFen = newFen;
        CurrentTurn = ParseTurnFromFen(newFen);
        WhiteTimeMs = whiteTimeMs;
        BlackTimeMs = blackTimeMs;
        IsInCheck = isCheck;
        CurrentBoard = Board.FromFen(newFen);

        GameBoardUpdated?.Invoke();
        TimeUpdated?.Invoke(whiteTimeMs, blackTimeMs);
        StatusMessage?.Invoke($"Opponent played: {move}");

        if (isCheckmate)
        {
            StatusMessage?.Invoke("Checkmate!");
            SetState(GameState.GameOver);
        }
        else if (isStalemate)
        {
            StatusMessage?.Invoke("Stalemate!");
            SetState(GameState.GameOver);
        }
        else if (isCheck)
        {
            StatusMessage?.Invoke("Check!");
        }
    }

    private void OnGameEnded(string gameId, int status, int reason, string winner,
        int ratingChange, int newRating)
    {
        if (gameId != CurrentGameId) return;

        Rating = newRating;
        Console.WriteLine($"[GameManager] Game ended. Reason: {(GameEndReason)reason}, Rating change: {ratingChange}, New rating: {newRating}");
        SetState(GameState.GameOver);
        PlayerInfoUpdated?.Invoke();

        var gameEndInfo = new GameEndInfo
        {
            Status = (GameStatus)status,
            Reason = (GameEndReason)reason,
            Winner = winner,
            RatingChange = ratingChange,
            NewRating = newRating
        };

        LastGameResult = gameEndInfo;
        GameEnded?.Invoke(gameEndInfo);

        // Start auto-return to lobby timer for draw or resign
        if (AutoReturnToLobbyEnabled)
        {
            var endReason = (GameEndReason)reason;
            if (endReason == GameEndReason.Agreement ||
                endReason == GameEndReason.Resignation ||
                endReason == GameEndReason.Timeout ||
                endReason == GameEndReason.Checkmate ||
                endReason == GameEndReason.Stalemate ||
                endReason == GameEndReason.InsufficientMaterial ||
                endReason == GameEndReason.FiftyMoveRule ||
                endReason == GameEndReason.ThreefoldRepetition)
            {
                StartReturnToLobbyTimer();
            }
        }
    }

    private void OnDrawOffered(string gameId, string offeredBy)
    {
        if (gameId != CurrentGameId) return;

        Console.WriteLine($"[GameManager] Draw offered by {offeredBy}");
        DrawOffered?.Invoke(offeredBy);
    }

    private void OnTimeUpdated(int whiteTimeMs, int blackTimeMs, int currentTurn)
    {
        WhiteTimeMs = whiteTimeMs;
        BlackTimeMs = blackTimeMs;
        CurrentTurn = (PieceColor)currentTurn;
        TimeUpdated?.Invoke(whiteTimeMs, blackTimeMs);
    }

    private void OnErrorReceived(string code, string message)
    {
        Console.WriteLine($"[GameManager] Server error: [{code}] {message}");
        ErrorOccurred?.Invoke($"Error ({code})", message);
    }

    #endregion

    #region State Management

    private void SetState(GameState newState)
    {
        if (CurrentState == newState) return;

        var oldState = CurrentState;
        CurrentState = newState;
        Console.WriteLine($"[GameManager] State: {oldState} -> {newState}");
        StateChanged?.Invoke(newState);
    }

    private void ResetPlayerInfo()
    {
        PlayerId = null;
        Username = null;
        Rating = 0;
        SessionToken = null;
    }

    private void ResetGameState()
    {
        CurrentGameId = null;
        OpponentName = null;
        OpponentRating = 0;
        CurrentFen = null;
        CurrentTurn = PieceColor.White;
        WhiteTimeMs = 0;
        BlackTimeMs = 0;
        IsInCheck = false;
        CurrentBoard = null;
        QueuePosition = 0;
        EstimatedWaitSeconds = 0;
        _isWaitingToReturnToLobby = false;
        _returnToLobbyTimer = 0;
    }

    private static PieceColor ParseTurnFromFen(string fen)
    {
        if (string.IsNullOrEmpty(fen))
            return PieceColor.White;

        var parts = fen.Split(' ');
        if (parts.Length >= 2)
        {
            return parts[1] == "b" ? PieceColor.Black : PieceColor.White;
        }
        return PieceColor.White;
    }

    #endregion

    #region Auto Return to Lobby

    private void StartReturnToLobbyTimer()
    {
        _isWaitingToReturnToLobby = true;
        _returnToLobbyTimer = ReturnToLobbyDelaySeconds;
        Console.WriteLine($"[GameManager] Will return to lobby in {ReturnToLobbyDelaySeconds} seconds");
        StatusMessage?.Invoke($"Returning to lobby in {(int)ReturnToLobbyDelaySeconds} seconds...");
    }

    public void CancelReturnToLobby()
    {
        _isWaitingToReturnToLobby = false;
        _returnToLobbyTimer = 0;
        Console.WriteLine("[GameManager] Auto-return to lobby cancelled");
    }

    public float GetReturnToLobbyTimeRemaining()
    {
        return _isWaitingToReturnToLobby ? _returnToLobbyTimer : 0;
    }

    public bool IsWaitingToReturnToLobby => _isWaitingToReturnToLobby;

    #endregion

    #region Utility Methods

    public bool IsMyTurn()
    {
        return CurrentState == GameState.InGame && CurrentTurn == PlayerColor;
    }

    public int GetMyTimeMs()
    {
        return PlayerColor == PieceColor.White ? WhiteTimeMs : BlackTimeMs;
    }

    public int GetOpponentTimeMs()
    {
        return PlayerColor == PieceColor.White ? BlackTimeMs : WhiteTimeMs;
    }

    public string FormatTime(int timeMs)
    {
        if (timeMs < 0) timeMs = 0;
        var totalSeconds = timeMs / 1000;
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;

        if (timeMs < 60000) // Less than 1 minute, show tenths
        {
            var tenths = (timeMs % 1000) / 100;
            return $"{seconds}.{tenths}";
        }

        return $"{minutes:D2}:{seconds:D2}";
    }

    public void Update(float deltaTime)
    {
        // Update network client
        Client.Update(deltaTime);

        // Update local clock display (for more responsive UI)
        if (CurrentState == GameState.InGame && IsMyTurn())
        {
            // Decrement local time for display purposes
            // The server is authoritative, this is just for smooth display
            if (PlayerColor == PieceColor.White)
            {
                WhiteTimeMs = Math.Max(0, WhiteTimeMs - (int)(deltaTime * 1000));
            }
            else
            {
                BlackTimeMs = Math.Max(0, BlackTimeMs - (int)(deltaTime * 1000));
            }
        }

        // Handle auto-return to lobby
        if (_isWaitingToReturnToLobby && CurrentState == GameState.GameOver)
        {
            _returnToLobbyTimer -= deltaTime;

            if (_returnToLobbyTimer <= 0)
            {
                _isWaitingToReturnToLobby = false;
                ReturnToLobby();
            }
        }
    }

    public void ReturnToLobby()
    {
        Console.WriteLine("[GameManager] Returning to lobby");
        _isWaitingToReturnToLobby = false;
        _returnToLobbyTimer = 0;
        ResetGameState();
        SetState(GameState.InLobby);
        StatusMessage?.Invoke("Welcome to the lobby!");
    }

    /// <summary>
    /// Immediately return to lobby, used when player manually clicks the button
    /// </summary>
    public void ReturnToLobbyImmediate()
    {
        CancelReturnToLobby();
        ReturnToLobby();
    }

    #endregion

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Console.WriteLine("[GameManager] Disposing...");
        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        DisconnectClientEvents();
        Client.Dispose();
        Console.WriteLine("[GameManager] Disposed");
    }
}

/// <summary>
/// Contains information about how a game ended.
/// </summary>
public class GameEndInfo
{
    public GameStatus Status { get; init; }
    public GameEndReason Reason { get; init; }
    public string? Winner { get; init; }
    public int RatingChange { get; init; }
    public int NewRating { get; init; }

    public string GetResultMessage(string? playerName)
    {
        string resultText = Reason switch
        {
            GameEndReason.Checkmate => Winner == playerName ? "VICTORY - CHECKMATE!" : "DEFEAT - CHECKMATE",
            GameEndReason.Resignation => Winner == playerName ? "VICTORY - OPPONENT RESIGNED" : "DEFEAT - RESIGNED",
            GameEndReason.Timeout => Winner == playerName ? "VICTORY - OPPONENT TIMED OUT" : "DEFEAT - TIME OUT",
            GameEndReason.Agreement => "DRAW - BY AGREEMENT",
            GameEndReason.Stalemate => "DRAW - STALEMATE",
            GameEndReason.InsufficientMaterial => "DRAW - INSUFFICIENT MATERIAL",
            GameEndReason.ThreefoldRepetition => "DRAW - THREEFOLD REPETITION",
            GameEndReason.FiftyMoveRule => "DRAW - FIFTY MOVE RULE",
            _ => "GAME OVER"
        };

        return resultText;
    }

    public bool IsVictory(string? playerName)
    {
        if (string.IsNullOrEmpty(Winner)) return false;
        return Winner == playerName;
    }

    public bool IsDraw()
    {
        return Reason == GameEndReason.Agreement ||
               Reason == GameEndReason.Stalemate ||
               Reason == GameEndReason.InsufficientMaterial ||
               Reason == GameEndReason.ThreefoldRepetition ||
               Reason == GameEndReason.FiftyMoveRule;
    }
}
