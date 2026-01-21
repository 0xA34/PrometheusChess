using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChessCore.Models;
using ChessCore.Network;

namespace PrometheusVulkan.Network;

public sealed class GameClient : IDisposable
{
    #region Events

    public event Action? Connected;
    public event Action<string>? Disconnected;
    public event Action<string>? ConnectionFailed;
    public event Action<string>? ConnectionRetrying;
    public event Action<string, string, int, string>? LoginSuccess; // playerId, username, rating, sessionToken
    public event Action<string>? LoginFailed;
    public event Action<string>? RegisterSuccess; // playerId
    public event Action<string>? RegisterFailed;
    public event Action<string, int, string, int>? MatchFound; // gameId, yourColor, opponentName, opponentRating
    public event Action<int, int, int>? QueueStatusUpdated; // status, position, estimatedWait
    public event Action<string, string, int, string, int, string, int>? GameStarted; // gameId, fen, yourColor, whiteName, whiteRating, blackName, blackRating
    public event Action<string, string, int, int, int, int, bool>? GameStateUpdated; // gameId, fen, currentTurn, status, whiteTimeMs, blackTimeMs, isCheck
    public event Action<bool, string, string, bool, bool, bool>? MoveResponseReceived; // success, message, newFen, isCheck, isCheckmate, isStalemate
    public event Action<string, string, bool, bool, bool, int, int>? OpponentMoved; // move, newFen, isCheck, isCheckmate, isStalemate, whiteTimeMs, blackTimeMs
    public event Action<string, int, int, string, int, int>? GameEnded; // gameId, status, reason, winner, ratingChange, newRating
    public event Action<string, string>? DrawOffered; // gameId, offeredBy
    public event Action<int, int, int>? TimeUpdated; // whiteTimeMs, blackTimeMs, currentTurn
    public event Action<string, string>? ErrorReceived; // code, message

    #endregion

    #region Properties

    public bool IsConnected => _client?.Connected ?? false;
    public string? SessionToken { get; private set; }
    public string? PlayerId { get; private set; }
    public string? Username { get; private set; }
    public int Rating { get; private set; }
    public string? CurrentGameId { get; private set; }
    public bool IsServerInMemoryMode { get; private set; }

    // Retry configuration
    public int MaxRetryAttempts { get; set; } = 5;
    public int RetryDelayMs { get; set; } = 2000;
    public bool AutoRetryOnDisconnect { get; set; } = false;

    #endregion

    #region Private Fields

    private TcpClient? _client;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _receiveTask;
    private readonly object _writeLock = new();
    private const int HeartbeatIntervalMs = 5000;
    private DateTime _lastHeartbeat = DateTime.UtcNow;
    private bool _isDisposed;
    private bool _isReconnecting;

    private string _serverHost = "127.0.0.1";
    private int _serverPort = 8787;

    #endregion

    #region Connection

    /// <summary>
    /// Connects to the game server with automatic retry on failure.
    /// </summary>
    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            Console.WriteLine("[GameClient] Already connected");
            return;
        }

        _serverHost = host;
        _serverPort = port;

        int attempt = 0;
        bool connected = false;

        while (!connected && attempt < MaxRetryAttempts && !cancellationToken.IsCancellationRequested)
        {
            attempt++;
            Console.WriteLine($"[GameClient] Connection attempt {attempt}/{MaxRetryAttempts} to {host}:{port}...");

            try
            {
                Cleanup();

                _client = new TcpClient();
                _client.NoDelay = true;

                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));

                await _client.ConnectAsync(host, port, connectCts.Token);

                _stream = _client.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8);
                _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };

                _cancellationTokenSource = new CancellationTokenSource();
                _receiveTask = Task.Run(() => ReceiveLoop(_cancellationTokenSource.Token));

                // Send initial connection message
                var connectMsg = new ConnectMessage
                {
                    ClientVersion = "0.0.1-alpha-in-prod",
                    ProtocolVersion = 1
                };
                await SendMessageAsync(connectMsg);

                connected = true;
                Console.WriteLine("[GameClient] Connection established successfully!");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("[GameClient] Connection cancelled by user");
                Cleanup();
                ConnectionFailed?.Invoke("Connection cancelled");
                return;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[GameClient] Connection attempt {attempt} timed out");
                Cleanup();

                if (attempt < MaxRetryAttempts)
                {
                    string retryMessage = $"Game server connection failed... Retrying ({attempt}/{MaxRetryAttempts})";
                    Console.WriteLine($"[GameClient] {retryMessage}");
                    ConnectionRetrying?.Invoke(retryMessage);
                    await Task.Delay(RetryDelayMs, cancellationToken);
                }
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[GameClient] Socket error on attempt {attempt}: {ex.Message}");
                Cleanup();

                if (attempt < MaxRetryAttempts)
                {
                    string retryMessage = $"Game server connection failed... Retrying ({attempt}/{MaxRetryAttempts})";
                    Console.WriteLine($"[GameClient] {retryMessage}");
                    ConnectionRetrying?.Invoke(retryMessage);
                    await Task.Delay(RetryDelayMs, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameClient] Connection error on attempt {attempt}: {ex.Message}");
                Cleanup();

                if (attempt < MaxRetryAttempts)
                {
                    string retryMessage = $"Game server connection failed... Retrying ({attempt}/{MaxRetryAttempts})";
                    Console.WriteLine($"[GameClient] {retryMessage}");
                    ConnectionRetrying?.Invoke(retryMessage);
                    await Task.Delay(RetryDelayMs, cancellationToken);
                }
            }
        }

        if (!connected)
        {
            string errorMessage = $"Failed to connect after {MaxRetryAttempts} attempts";
            Console.WriteLine($"[GameClient] {errorMessage}");
            ConnectionFailed?.Invoke(errorMessage);
        }
    }

    /// <summary>
    /// Attempts to reconnect to the last known server.
    /// </summary>
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isReconnecting)
        {
            Console.WriteLine("[GameClient] Already attempting to reconnect");
            return;
        }

        _isReconnecting = true;
        try
        {
            Console.WriteLine("[GameClient] Attempting to reconnect...");
            await ConnectAsync(_serverHost, _serverPort, cancellationToken);
        }
        finally
        {
            _isReconnecting = false;
        }
    }

    public void Disconnect()
    {
        Console.WriteLine("[GameClient] Disconnecting...");
        _cancellationTokenSource?.Cancel();
        Cleanup();
        Disconnected?.Invoke("Disconnected by user");
    }

    private void Cleanup()
    {
        try
        {
            _cancellationTokenSource?.Cancel();
            _reader?.Dispose();
            _writer?.Dispose();
            _stream?.Dispose();
            _client?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameClient] Cleanup error: {ex.Message}");
        }
        finally
        {
            _reader = null;
            _writer = null;
            _stream = null;
            _client = null;
            _cancellationTokenSource = null;
        }
    }

    #endregion

    #region Authentication

    public async Task RegisterAsync(string username, string email, string password)
    {
        var message = new RegisterMessage
        {
            Username = username,
            Email = email,
            PasswordHash = HashPassword(password)
        };

        await SendMessageAsync(message);
    }

    public async Task LoginAsync(string username, string password)
    {
        var message = new LoginMessage
        {
            Username = username,
            PasswordHash = HashPassword(password)
        };

        await SendMessageAsync(message);
    }

    public async Task LoginWithTokenAsync(string sessionToken)
    {
        var message = new LoginMessage
        {
            Username = "",
            PasswordHash = "",
            SessionToken = sessionToken
        };

        await SendMessageAsync(message);
    }

    private static string HashPassword(string password)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        return Convert.ToBase64String(bytes);
    }

    #endregion

    #region Matchmaking

    public async Task FindMatchAsync(TimeControlType timeControl = TimeControlType.Rapid,
        int initialTimeMs = 600000, int incrementMs = 0, int ratingRange = 200)
    {
        if (string.IsNullOrEmpty(SessionToken))
        {
            Console.WriteLine("[GameClient] Cannot find match: not logged in");
            return;
        }

        var message = new FindMatchMessage
        {
            SessionToken = SessionToken,
            TimeControl = timeControl,
            InitialTimeMs = initialTimeMs,
            IncrementMs = incrementMs,
            RatingRange = ratingRange
        };

        await SendMessageAsync(message);
    }

    public async Task CancelFindMatchAsync()
    {
        if (string.IsNullOrEmpty(SessionToken))
            return;

        var message = new CancelFindMatchMessage
        {
            SessionToken = SessionToken
        };

        await SendMessageAsync(message);
    }

    #endregion

    #region Game Actions

    public async Task SendMoveAsync(string from, string to, PieceType? promotion = null)
    {
        if (string.IsNullOrEmpty(SessionToken) || string.IsNullOrEmpty(CurrentGameId))
        {
            Console.WriteLine("[GameClient] Cannot send move: not in game");
            return;
        }

        var message = new MoveRequestMessage
        {
            SessionToken = SessionToken,
            GameId = CurrentGameId,
            From = from,
            To = to,
            Promotion = promotion?.ToString(),
            ClientTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        await SendMessageAsync(message);
    }

    public async Task ResignAsync()
    {
        if (string.IsNullOrEmpty(SessionToken) || string.IsNullOrEmpty(CurrentGameId))
            return;

        var message = new ResignMessage
        {
            SessionToken = SessionToken,
            GameId = CurrentGameId
        };

        await SendMessageAsync(message);
    }

    public async Task OfferDrawAsync()
    {
        if (string.IsNullOrEmpty(SessionToken) || string.IsNullOrEmpty(CurrentGameId))
            return;

        var message = new OfferDrawMessage
        {
            SessionToken = SessionToken,
            GameId = CurrentGameId
        };

        await SendMessageAsync(message);
    }

    public async Task AcceptDrawAsync()
    {
        if (string.IsNullOrEmpty(SessionToken) || string.IsNullOrEmpty(CurrentGameId))
            return;

        var message = new AcceptDrawMessage
        {
            SessionToken = SessionToken,
            GameId = CurrentGameId
        };

        await SendMessageAsync(message);
    }

    public async Task DeclineDrawAsync()
    {
        if (string.IsNullOrEmpty(SessionToken) || string.IsNullOrEmpty(CurrentGameId))
            return;

        var message = new DeclineDrawMessage
        {
            SessionToken = SessionToken,
            GameId = CurrentGameId
        };

        await SendMessageAsync(message);
    }

    #endregion

    #region Network Communication

    private async Task SendMessageAsync(NetworkMessage message)
    {
        if (_writer == null || !IsConnected)
        {
            Console.WriteLine("[GameClient] Cannot send message: not connected");
            return;
        }

        try
        {
            var json = message.ToJson();
            lock (_writeLock)
            {
                _writer.WriteLine(json);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameClient] Error sending message: {ex.Message}");
            HandleConnectionLost("Send error: " + ex.Message);
        }
    }

    private void SendHeartbeat()
    {
        if (_writer == null || !IsConnected) return;

        try
        {
            var heartbeat = new HeartbeatMessage
            {
                ClientTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            var json = heartbeat.ToJson();
            lock (_writeLock)
            {
                _writer.WriteLine(json);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameClient] Heartbeat error: {ex.Message}");
            HandleConnectionLost("Heartbeat error: " + ex.Message);
        }
    }

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        Console.WriteLine("[GameClient] Starting receive loop");

        try
        {
            while (!cancellationToken.IsCancellationRequested && _reader != null)
            {
                var line = await _reader.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    Console.WriteLine("[GameClient] Server closed connection");
                    HandleConnectionLost("Server closed connection");
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    try
                    {
                        ProcessMessage(line);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[GameClient] Error processing message: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[GameClient] Receive loop cancelled");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[GameClient] IO error in receive loop: {ex.Message}");
            HandleConnectionLost("Connection lost: " + ex.Message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameClient] Error in receive loop: {ex.Message}");
            HandleConnectionLost("Error: " + ex.Message);
        }
    }

    private void HandleConnectionLost(string reason)
    {
        Console.WriteLine($"[GameClient] Connection lost: {reason}");
        Cleanup();
        Disconnected?.Invoke(reason);

        // Auto-reconnect if enabled
        if (AutoRetryOnDisconnect && !_isDisposed)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(RetryDelayMs);
                if (!IsConnected && !_isDisposed)
                {
                    ConnectionRetrying?.Invoke("Game server connection failed... Retrying");
                    await ReconnectAsync();
                }
            });
        }
    }

    private void ProcessMessage(string json)
    {
        var message = NetworkMessage.FromJson(json);
        if (message == null)
        {
            Console.WriteLine($"[GameClient] Unknown message: {json}");
            return;
        }

        switch (message)
        {
            case ConnectResponseMessage response:
                HandleConnectResponse(response);
                break;
            case LoginResponseMessage response:
                HandleLoginResponse(response);
                break;
            case RegisterResponseMessage response:
                HandleRegisterResponse(response);
                break;
            case MatchFoundMessage response:
                HandleMatchFound(response);
                break;
            case QueueStatusMessage response:
                HandleQueueStatus(response);
                break;
            case GameStartMessage response:
                HandleGameStart(response);
                break;
            case GameStateMessage response:
                HandleGameState(response);
                break;
            case MoveResponseMessage response:
                HandleMoveResponse(response);
                break;
            case MoveNotificationMessage response:
                HandleMoveNotification(response);
                break;
            case GameEndMessage response:
                HandleGameEnd(response);
                break;
            case DrawOfferedMessage response:
                HandleDrawOffered(response);
                break;
            case TimeUpdateMessage response:
                HandleTimeUpdate(response);
                break;
            case HeartbeatAckMessage:
                // Heartbeat acknowledged, nothing to do
                break;
            case ErrorMessage response:
                HandleError(response);
                break;
            default:
                Console.WriteLine($"[GameClient] Unhandled message type: {message.Type}");
                break;
        }
    }

    #endregion

    #region Message Handlers

    private void HandleConnectResponse(ConnectResponseMessage response)
    {
        if (response.Success)
        {
            Console.WriteLine($"[GameClient] Connected to server: {response.ServerVersion}");
            IsServerInMemoryMode = response.IsMemoryMode;
            if (IsServerInMemoryMode)
            {
                Console.WriteLine("[GameClient] Server is running in MEMORY MODE - data will not be persisted!");
            }
            Connected?.Invoke();
        }
        else
        {
            Console.WriteLine($"[GameClient] Connection rejected: {response.Message}");
            ConnectionFailed?.Invoke(response.Message ?? "Connection rejected");
            Cleanup();
        }
    }

    private void HandleLoginResponse(LoginResponseMessage response)
    {
        if (response.Success)
        {
            PlayerId = response.PlayerId;
            Username = response.Username;
            Rating = response.Rating;
            SessionToken = response.SessionToken;
            Console.WriteLine($"[GameClient] Logged in as {Username} (Rating: {Rating})");
            LoginSuccess?.Invoke(PlayerId ?? "", Username ?? "", Rating, SessionToken ?? "");
        }
        else
        {
            Console.WriteLine($"[GameClient] Login failed: {response.Message}");
            LoginFailed?.Invoke(response.Message ?? "Login failed");
        }
    }

    private void HandleRegisterResponse(RegisterResponseMessage response)
    {
        if (response.Success)
        {
            Console.WriteLine($"[GameClient] Registration successful: {response.PlayerId}");
            RegisterSuccess?.Invoke(response.PlayerId ?? "");
        }
        else
        {
            Console.WriteLine($"[GameClient] Registration failed: {response.Message}");
            RegisterFailed?.Invoke(response.Message ?? "Registration failed");
        }
    }

    private void HandleMatchFound(MatchFoundMessage response)
    {
        CurrentGameId = response.GameId;
        Console.WriteLine($"[GameClient] Match found! Game: {response.GameId}, Opponent: {response.OpponentName}");
        MatchFound?.Invoke(response.GameId, (int)response.YourColor, response.OpponentName, response.OpponentRating);
    }

    private void HandleQueueStatus(QueueStatusMessage response)
    {
        QueueStatusUpdated?.Invoke((int)response.Status, response.QueuePosition ?? 0, response.EstimatedWaitSeconds ?? 0);
    }

    private void HandleGameStart(GameStartMessage response)
    {
        CurrentGameId = response.GameId;
        Console.WriteLine($"[GameClient] Game started! ID: {response.GameId}");
        GameStarted?.Invoke(
            response.GameId,
            response.Fen,
            (int)response.YourColor,
            response.WhitePlayer?.Username ?? "White",
            response.WhitePlayer?.Rating ?? 1200,
            response.BlackPlayer?.Username ?? "Black",
            response.BlackPlayer?.Rating ?? 1200
        );
    }

    private void HandleGameState(GameStateMessage response)
    {
        GameStateUpdated?.Invoke(
            response.GameId,
            response.Fen,
            (int)response.CurrentTurn,
            (int)response.Status,
            response.WhiteTimeMs,
            response.BlackTimeMs,
            response.IsCheck
        );
    }

    private void HandleMoveResponse(MoveResponseMessage response)
    {
        MoveResponseReceived?.Invoke(
            response.Success,
            response.Message ?? "",
            response.NewFen ?? "",
            response.IsCheck,
            response.IsCheckmate,
            response.IsStalemate
        );
    }

    private void HandleMoveNotification(MoveNotificationMessage response)
    {
        OpponentMoved?.Invoke(
            response.Move,
            response.NewFen,
            response.IsCheck,
            response.IsCheckmate,
            response.IsStalemate,
            response.WhiteTimeMs,
            response.BlackTimeMs
        );
    }

    private void HandleGameEnd(GameEndMessage response)
    {
        Console.WriteLine($"[GameClient] Game ended: {response.Status}, Reason: {response.Reason}");
        string winnerStr = response.Winner?.ToString() ?? "";
        GameEnded?.Invoke(
            response.GameId,
            (int)response.Status,
            (int)response.Reason,
            winnerStr,
            response.RatingChange,
            response.NewRating
        );
        CurrentGameId = null;
    }

    private void HandleDrawOffered(DrawOfferedMessage response)
    {
        DrawOffered?.Invoke(response.GameId, response.OfferedBy);
    }

    private void HandleTimeUpdate(TimeUpdateMessage response)
    {
        TimeUpdated?.Invoke(response.WhiteTimeMs, response.BlackTimeMs, (int)response.CurrentTurn);
    }

    private void HandleError(ErrorMessage response)
    {
        Console.WriteLine($"[GameClient] Server error: [{response.Code}] {response.Message}");
        ErrorReceived?.Invoke(response.Code, response.Message);
    }

    #endregion

    #region Update

    public void Update(float deltaTime)
    {
        // Send heartbeat periodically
        if (IsConnected && (DateTime.UtcNow - _lastHeartbeat).TotalMilliseconds > HeartbeatIntervalMs)
        {
            SendHeartbeat();
            _lastHeartbeat = DateTime.UtcNow;
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Console.WriteLine("[GameClient] Disposing...");

        try
        {
            _cancellationTokenSource?.Cancel();
            Cleanup();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameClient] Error during dispose: {ex.Message}");
        }

        Console.WriteLine("[GameClient] Disposed");
    }

    #endregion
}
