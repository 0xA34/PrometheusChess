using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ChessCore.Network;
using ChessCore.Security;
using PrometheusServer.Services;
using Microsoft.Extensions.Logging;
using PrometheusServer.Data;
using PrometheusServer.Logging;

namespace PrometheusServer.Core;

/// <summary>
/// Main TCP server for handling game connections.
/// This is the server-authoritative game server that validates all moves
/// and maintains the true game state.
/// </summary>
public sealed class PrometheusGameServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly ILogger<PrometheusGameServer> _logger;
    private readonly SecurityManager _securityManager;
    private readonly SessionManager _sessionManager;
    private readonly PlayerManager _playerManager;
    private readonly MatchmakingService _matchmakingService;
    private readonly GameManager _gameManager;
    private readonly ServerConfiguration _config;
    private readonly DatabaseConfiguration _dbConfig;

    private readonly ConcurrentDictionary<string, ClientConnection> _connections = new();
    private readonly CancellationTokenSource _serverCts = new();

    private bool _isRunning;
    private Task? _acceptTask;
    private Task? _heartbeatTask;

    public bool IsRunning => _isRunning;
    public int ConnectionCount => _connections.Count;

    /// <summary>
    /// Creates a new PrometheusServer instance
    /// </summary>
    public PrometheusGameServer(
        ServerConfiguration config,
        DatabaseConfiguration dbConfig,
        SecurityManager securityManager,
        SessionManager sessionManager,
        PlayerManager playerManager,
        MatchmakingService matchmakingService,
        GameManager gameManager,
        ILogger<PrometheusGameServer> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _dbConfig = dbConfig ?? throw new ArgumentNullException(nameof(dbConfig));
        _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _playerManager = playerManager ?? throw new ArgumentNullException(nameof(playerManager));
        _matchmakingService = matchmakingService ?? throw new ArgumentNullException(nameof(matchmakingService));
        _gameManager = gameManager ?? throw new ArgumentNullException(nameof(gameManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var bindAddress = IPAddress.Parse(_config.BindAddress);
        _listener = new TcpListener(bindAddress, _config.Port);

        // Wire up events
        _matchmakingService.MatchFound += OnMatchFound;
        _gameManager.GameEnded += OnGameEnded;
    }

    /// <summary>
    /// Starts the server and begins accepting connections
    /// </summary>
    public async Task StartAsync()
    {
        if (_isRunning)
        {
            _logger.LogWarning("Server is already running");
            return;
        }

        try
        {
            _listener.Start(_config.MaxConnections);
            _isRunning = true;

            _logger.LogInformation("Prometheus Server initialised and listening");

            // Start accepting connections
            _acceptTask = AcceptConnectionsAsync(_serverCts.Token);

            // Start heartbeat monitoring
            _heartbeatTask = HeartbeatMonitorAsync(_serverCts.Token);

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start server");
            throw;
        }
    }

    /// <summary>
    /// Stops the server and disconnects all clients
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _logger.LogWarning("Server stop requested - disconnecting all clients...");
        _isRunning = false;

        // Signal cancellation
        _serverCts.Cancel();

        // Stop listening
        _listener.Stop();

        // Disconnect all clients
        foreach (var connection in _connections.Values)
        {
            await DisconnectClientAsync(connection, "Server shutting down");
        }

        _connections.Clear();

        // Wait for background tasks
        if (_acceptTask != null)
            await Task.WhenAny(_acceptTask, Task.Delay(5000));

        if (_heartbeatTask != null)
            await Task.WhenAny(_heartbeatTask, Task.Delay(5000));

        _logger.LogInformation("Server stopped successfully");
    }

    /// <summary>
    /// Main loop for accepting incoming connections
    /// </summary>
    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isRunning)
        {
            try
            {
                var tcpClient = await _listener.AcceptTcpClientAsync(ct);

                // Note that, only expand if you want really to do it in production
                // However, this really not critical, maybe I'll just put 1000.
                if (_connections.Count >= _config.MaxConnections)
                {
                    _logger.LogWarning("Connection limit reached, rejecting client");
                    tcpClient.Close();
                    continue;
                }

                // Handle the new connection in a separate task
                _ = Task.Run(() => HandleNewConnectionAsync(tcpClient, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_isRunning)
                {
                    _logger.LogError(ex, "Error accepting connection");
                }
            }
        }
    }

    /// <summary>
    /// Handles a new client connection
    /// </summary>
    private async Task HandleNewConnectionAsync(TcpClient tcpClient, CancellationToken ct)
    {
        var remoteEndpoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "Unknown";
        var connection = new ClientConnection(tcpClient, _logger);

        ServerLogger.LogPlayerConnected(_logger, connection.ConnectionId, remoteEndpoint);

        try
        {
            // Add to connections dictionary
            if (!_connections.TryAdd(connection.ConnectionId, connection))
            {
                _logger.LogError($"Failed to add connection {connection.ConnectionId}");
                connection.Dispose();
                return;
            }

            // Start receiving messages
            await ReceiveMessagesAsync(connection, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling connection {connection.ConnectionId}");
        }
        finally
        {
            await DisconnectClientAsync(connection, "Connection closed");
        }
    }

    /// <summary>
    /// Receives and processes messages from a client
    /// </summary>
    private async Task ReceiveMessagesAsync(ClientConnection connection, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && connection.IsConnected)
        {
            try
            {
                var message = await connection.ReceiveMessageAsync(ct);

                if (message == null)
                {
                    _logger.LogDebug($"Client {connection.ConnectionId} disconnected");
                    break;
                }

                // Rate limiting check
                // This looks really bad to be honest, might as well as check later.
                if (!_securityManager.CheckRateLimit(connection.ConnectionId, _config.MaxRequestsPerMinute, 60))
                {
                    _logger.LogWarning($"Rate limit exceeded for {connection.ConnectionId}");
                    await connection.SendMessageAsync(new ErrorMessage
                    {
                        Code = "RATE_LIMITED",
                        Message = "Too many requests. Please slow down."
                    });
                    continue;
                }

                // Process the message
                await ProcessMessageAsync(connection, message);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error receiving message from {connection.ConnectionId}");
                break;
            }
        }
    }

    /// <summary>
    /// Processes an incoming message from a client
    /// </summary>
    private async Task ProcessMessageAsync(ClientConnection connection, NetworkMessage message)
    {
        _logger.LogDebug($"Received {message.Type} from {connection.ConnectionId}");

        try
        {
            switch (message.Type)
            {
                case MessageType.Connect:
                    await HandleConnectAsync(connection, (ConnectMessage)message);
                    break;

                case MessageType.Heartbeat:
                    await HandleHeartbeatAsync(connection, (HeartbeatMessage)message);
                    break;

                case MessageType.Login:
                    await HandleLoginAsync(connection, (LoginMessage)message);
                    break;

                case MessageType.Register:
                    await HandleRegisterAsync(connection, (RegisterMessage)message);
                    break;

                case MessageType.FindMatch:
                    await HandleFindMatchAsync(connection, (FindMatchMessage)message);
                    break;

                case MessageType.CancelFindMatch:
                    await HandleCancelFindMatchAsync(connection, (CancelFindMatchMessage)message);
                    break;

                case MessageType.MoveRequest:
                    await HandleMoveRequestAsync(connection, (MoveRequestMessage)message);
                    break;

                case MessageType.Resign:
                    await HandleResignAsync(connection, (ResignMessage)message);
                    break;

                case MessageType.OfferDraw:
                    await HandleOfferDrawAsync(connection, (OfferDrawMessage)message);
                    break;

                case MessageType.AcceptDraw:
                    await HandleAcceptDrawAsync(connection, (AcceptDrawMessage)message);
                    break;

                case MessageType.DeclineDraw:
                    await HandleDeclineDrawAsync(connection, (DeclineDrawMessage)message);
                    break;

                default:
                    _logger.LogWarning($"Unknown message type: {message.Type}");
                    await connection.SendMessageAsync(new ErrorMessage
                    {
                        Code = "UNKNOWN_MESSAGE",
                        Message = $"Unknown message type: {message.Type}"
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error processing message {message.Type}");
            await connection.SendMessageAsync(new ErrorMessage
            {
                Code = "INTERNAL_ERROR",
                Message = "An error occurred processing your request"
            });
        }
    }

    #region Message Handlers

    private async Task HandleConnectAsync(ClientConnection connection, ConnectMessage message)
    {
        _logger.LogDebug("Client {ConnectionId} connecting: version {Version}, protocol {Protocol}",
            connection.ConnectionId, message.ClientVersion, message.ProtocolVersion);

        await connection.SendMessageAsync(new ConnectResponseMessage
        {
            Success = true,
            ServerVersion = "0.0.1-alpha-indev",
            ConnectionId = connection.ConnectionId,
            Message = "Hello Prometheus!",
            IsMemoryMode = _dbConfig.UseInMemory
        });
    }

    private async Task HandleHeartbeatAsync(ClientConnection connection, HeartbeatMessage message)
    {
        connection.UpdateLastActivity();

        await connection.SendMessageAsync(new HeartbeatAckMessage
        {
            ClientTime = message.ClientTime,
            ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    // The whole ban system is not implemented.
    // However, this is just a placebo.
    // You actually need a database to handle all of these by the way.
    private async Task HandleLoginAsync(ClientConnection connection, LoginMessage message)
    {
        // Get client IP for session tracking
        var clientIp = connection.RemoteEndpoint;

        // Try session resumption first
        if (!string.IsNullOrEmpty(message.SessionToken))
        {
            var sessionResult = await _sessionManager.ValidateSessionAsync(message.SessionToken);
            if (sessionResult.IsValid && sessionResult.PlayerId != null)
            {
                var existingPlayer = await _playerManager.GetPlayerByIdAsync(sessionResult.PlayerId);
                if (existingPlayer != null)
                {
                    // Check if player is banned
                    if (existingPlayer.IsBanned)
                    {
                        await _sessionManager.RevokeSessionAsync(message.SessionToken, "Player banned");
                        await connection.SendMessageAsync(new LoginResponseMessage
                        {
                            Success = false,
                            ErrorCode = "ACCOUNT_BANNED",
                            Message = $"Your account has been banned: {existingPlayer.BanReason ?? "No reason provided"}"
                        });
                        return;
                    }

                    // Disconnect any existing connections for this player
                    await DisconnectExistingSessionsAsync(existingPlayer.PlayerId, connection.ConnectionId);

                    // Resume session
                    connection.SetPlayerId(existingPlayer.PlayerId, existingPlayer.Username);

                    await connection.SendMessageAsync(new LoginResponseMessage
                    {
                        Success = true,
                        PlayerId = existingPlayer.PlayerId,
                        Username = existingPlayer.Username,
                        SessionToken = message.SessionToken,
                        TokenExpiry = sessionResult.ExpiresAt.HasValue
                            ? new DateTimeOffset(sessionResult.ExpiresAt.Value).ToUnixTimeMilliseconds()
                            : 0,
                        Rating = existingPlayer.Rating,
                        Message = "Session resumed"
                    });

                    _logger.LogInformation("Session resumed for {Username} from {IP}",
                        existingPlayer.Username, clientIp);
                    return;
                }
            }
        }

        // Normal login
        var player = await _playerManager.AuthenticateAsync(message.Username, message.PasswordHash);

        if (player == null)
        {
            await connection.SendMessageAsync(new LoginResponseMessage
            {
                Success = false,
                ErrorCode = "INVALID_CREDENTIALS",
                Message = "Invalid username or password"
            });
            return;
        }

        // Check if player is banned (AuthenticateAsync should also check, but double-check here)
        if (player.IsBanned)
        {
            await connection.SendMessageAsync(new LoginResponseMessage
            {
                Success = false,
                ErrorCode = "ACCOUNT_BANNED",
                Message = $"Your account has been banned: {player.BanReason ?? "No reason provided"}"
            });
            return;
        }

        // Create new session using SessionManager
        var sessionCreation = await _sessionManager.CreateSessionAsync(
            player.PlayerId,
            player.Username,
            clientIp,
            null); // userAgent not available at TCP level

        if (!sessionCreation.Success || sessionCreation.Token == null)
        {
            await connection.SendMessageAsync(new LoginResponseMessage
            {
                Success = false,
                ErrorCode = "SESSION_ERROR",
                Message = sessionCreation.ErrorMessage ?? "Failed to create session"
            });
            return;
        }

        // Disconnect any existing connections for this player
        await DisconnectExistingSessionsAsync(player.PlayerId, connection.ConnectionId);

        connection.SetPlayerId(player.PlayerId, player.Username);

        await connection.SendMessageAsync(new LoginResponseMessage
        {
            Success = true,
            PlayerId = player.PlayerId,
            Username = player.Username,
            SessionToken = sessionCreation.Token,
            TokenExpiry = sessionCreation.ExpiresAtUnix ?? 0,
            Rating = player.Rating,
            Message = "Login successful"
        });

        ServerLogger.LogPlayerLogin(_logger, player.Username, player.Rating);
    }

    private async Task HandleRegisterAsync(ClientConnection connection, RegisterMessage message)
    {
        // Validate inputs
        var usernameValidation = SecurityManager.ValidateUsername(message.Username);
        if (!usernameValidation.IsValid)
        {
            await connection.SendMessageAsync(new RegisterResponseMessage
            {
                Success = false,
                ErrorCode = "INVALID_USERNAME",
                Message = usernameValidation.ErrorMessage
            });
            return;
        }

        var emailValidation = SecurityManager.ValidateEmail(message.Email);
        if (!emailValidation.IsValid)
        {
            await connection.SendMessageAsync(new RegisterResponseMessage
            {
                Success = false,
                ErrorCode = "INVALID_EMAIL",
                Message = emailValidation.ErrorMessage
            });
            return;
        }

        // Try to register
        var result = await _playerManager.RegisterAsync(message.Username, message.Email, message.PasswordHash);

        if (!result.Success)
        {
            await connection.SendMessageAsync(new RegisterResponseMessage
            {
                Success = false,
                ErrorCode = result.ErrorCode,
                Message = result.Message
            });
            return;
        }

        await connection.SendMessageAsync(new RegisterResponseMessage
        {
            Success = true,
            PlayerId = result.PlayerId,
            Message = "Registration successful. You can now log in."
        });

        ServerLogger.LogPlayerRegistered(_logger, message.Username);
    }

    // Just simple matchmaking.
    private async Task HandleFindMatchAsync(ClientConnection connection, FindMatchMessage message)
    {
        // Validate session using SessionManager (full validation including DB check)
        var sessionResult = await _sessionManager.ValidateSessionAsync(message.SessionToken);
        if (!sessionResult.IsValid)
        {
            await connection.SendMessageAsync(new ErrorMessage
            {
                Code = "INVALID_TOKEN",
                Message = sessionResult.ErrorMessage ?? "Invalid or expired session token"
            });
            return;
        }

        if (connection.PlayerId == null)
        {
            await connection.SendMessageAsync(new ErrorMessage
            {
                Code = "NOT_LOGGED_IN",
                Message = "You must be logged in to find a match"
            });
            return;
        }

        // Add to matchmaking queue
        var player = _playerManager.GetPlayerById(connection.PlayerId);
        if (player == null)
        {
            await connection.SendMessageAsync(new ErrorMessage
            {
                Code = "PLAYER_NOT_FOUND",
                Message = "Player not found"
            });
            return;
        }

        _matchmakingService.EnqueuePlayer(new MatchmakingRequest
        {
            PlayerId = connection.PlayerId,
            Username = player.Username,
            Rating = player.Rating,
            TimeControl = message.TimeControl,
            InitialTimeMs = message.InitialTimeMs,
            IncrementMs = message.IncrementMs,
            RatingRange = message.RatingRange,
            ConnectionId = connection.ConnectionId
        });

        var queueSize = _matchmakingService.PlayersInQueue;

        await connection.SendMessageAsync(new QueueStatusMessage
        {
            Status = ChessCore.Models.QueueStatus.Searching,
            PlayersInQueue = queueSize + 1
        });

        ServerLogger.LogQueueJoin(_logger, player.Username, player.Rating, message.TimeControl.ToString(), queueSize + 1);
    }

    private async Task HandleCancelFindMatchAsync(ClientConnection connection, CancelFindMatchMessage message)
    {
        if (connection.PlayerId == null)
            return;

        _matchmakingService.DequeuePlayer(connection.PlayerId);

        await connection.SendMessageAsync(new QueueStatusMessage
        {
            Status = ChessCore.Models.QueueStatus.Cancelled
        });

        _logger.LogDebug("Player {Username} left matchmaking queue", connection.Username);
    }

    private async Task HandleMoveRequestAsync(ClientConnection connection, MoveRequestMessage message)
    {
        // Validate session using quick JWT validation (for performance in high-frequency operations)
        var tokenResult = _sessionManager.ValidateTokenQuick(message.SessionToken);
        if (!tokenResult.IsValid || connection.PlayerId == null)
        {
            await connection.SendMessageAsync(new ErrorMessage
            {
                Code = "INVALID_TOKEN",
                Message = tokenResult.ErrorMessage ?? "Invalid or expired session token"
            });
            return;
        }

        // Process the move
        var result = await _gameManager.ProcessMoveAsync(
            message.GameId,
            connection.PlayerId,
            message.From,
            message.To,
            message.Promotion,
            message.ExpectedSequence
        );

        await connection.SendMessageAsync(result);

        // If valid, notify opponent
        if (result.Success)
        {
            var game = _gameManager.GetGame(message.GameId);
            if (game != null)
            {
                var opponentId = game.WhitePlayer.PlayerId == connection.PlayerId
                    ? game.BlackPlayer.PlayerId
                    : game.WhitePlayer.PlayerId;

                var opponentConnection = GetConnectionByPlayerId(opponentId);
                if (opponentConnection != null)
                {
                    await opponentConnection.SendMessageAsync(new MoveNotificationMessage
                    {
                        GameId = message.GameId,
                        Move = result.Move ?? $"{message.From}{message.To}",
                        NewFen = result.NewFen ?? "",
                        IsCheck = result.IsCheck,
                        IsCheckmate = result.IsCheckmate,
                        IsStalemate = result.IsStalemate,
                        WhiteTimeMs = result.WhiteTimeMs,
                        BlackTimeMs = result.BlackTimeMs,
                        MoveSequence = result.MoveSequence
                    });
                }
            }
        }
    }

    private async Task HandleResignAsync(ClientConnection connection, ResignMessage message)
    {
        var tokenResult = _sessionManager.ValidateTokenQuick(message.SessionToken);
        if (!tokenResult.IsValid || connection.PlayerId == null)
            return;

        var gameEndResult = _gameManager.HandleResignation(message.GameId, connection.PlayerId);

        if (gameEndResult != null)
        {
            await BroadcastToGame(message.GameId, gameEndResult);
        }
    }

    private async Task HandleOfferDrawAsync(ClientConnection connection, OfferDrawMessage message)
    {
        var tokenResult = _sessionManager.ValidateTokenQuick(message.SessionToken);
        if (!tokenResult.IsValid || connection.PlayerId == null)
            return;

        var game = _gameManager.GetGame(message.GameId);
        if (game == null)
            return;

        var opponentId = game.WhitePlayer.PlayerId == connection.PlayerId
            ? game.BlackPlayer.PlayerId
            : game.WhitePlayer.PlayerId;

        var opponentConnection = GetConnectionByPlayerId(opponentId);
        if (opponentConnection != null)
        {
            await opponentConnection.SendMessageAsync(new DrawOfferedMessage
            {
                GameId = message.GameId,
                OfferedBy = connection.Username ?? "Unknown"
            });
        }
    }

    private async Task HandleAcceptDrawAsync(ClientConnection connection, AcceptDrawMessage message)
    {
        var tokenResult = _sessionManager.ValidateTokenQuick(message.SessionToken);
        if (!tokenResult.IsValid || connection.PlayerId == null)
            return;

        var gameEndResult = _gameManager.HandleDrawAccepted(message.GameId);

        if (gameEndResult != null)
        {
            await BroadcastToGame(message.GameId, gameEndResult);
        }
    }

    private async Task HandleDeclineDrawAsync(ClientConnection connection, DeclineDrawMessage message)
    {
        var tokenResult = _sessionManager.ValidateTokenQuick(message.SessionToken);
        if (!tokenResult.IsValid || connection.PlayerId == null)
            return;

        // Just notify the opponent that draw was declined
        var game = _gameManager.GetGame(message.GameId);
        if (game == null)
            return;

        var opponentId = game.WhitePlayer.PlayerId == connection.PlayerId
            ? game.BlackPlayer.PlayerId
            : game.WhitePlayer.PlayerId;

        var opponentConnection = GetConnectionByPlayerId(opponentId);
        if (opponentConnection != null)
        {
            await opponentConnection.SendMessageAsync(new ErrorMessage
            {
                Code = "DRAW_DECLINED",
                Message = "Your draw offer was declined"
            });
        }
    }

    #endregion

    #region Event Handlers

    private async void OnMatchFound(object? sender, MatchFoundEventArgs e)
    {
        ServerLogger.LogMatchFound(
            _logger,
            e.Player1.Username,
            e.Player1.Rating,
            e.Player2.Username,
            e.Player2.Rating,
            e.TimeControl.ToString());

        // Create the game
        var game = _gameManager.CreateGame(e.Player1, e.Player2, e.InitialTimeMs, e.IncrementMs, e.TimeControl);

        // Notify both players
        var player1Connection = GetConnectionByPlayerId(e.Player1.PlayerId);
        var player2Connection = GetConnectionByPlayerId(e.Player2.PlayerId);

        var gameStartForWhite = new GameStartMessage
        {
            GameId = game.GameId,
            YourColor = ChessCore.Models.PieceColor.White,
            WhitePlayer = new PlayerInfoDto
            {
                PlayerId = game.WhitePlayer.PlayerId,
                Username = game.WhitePlayer.Username,
                Rating = game.WhitePlayer.Rating
            },
            BlackPlayer = new PlayerInfoDto
            {
                PlayerId = game.BlackPlayer.PlayerId,
                Username = game.BlackPlayer.Username,
                Rating = game.BlackPlayer.Rating
            },
            WhiteTimeMs = game.WhiteTimeMs,
            BlackTimeMs = game.BlackTimeMs,
            IncrementMs = e.IncrementMs
        };

        var gameStartForBlack = new GameStartMessage
        {
            GameId = game.GameId,
            YourColor = ChessCore.Models.PieceColor.Black,
            WhitePlayer = gameStartForWhite.WhitePlayer,
            BlackPlayer = gameStartForWhite.BlackPlayer,
            WhiteTimeMs = game.WhiteTimeMs,
            BlackTimeMs = game.BlackTimeMs,
            IncrementMs = e.IncrementMs
        };

        if (player1Connection != null)
        {
            // Player1 is white in this case (based on how CreateGame assigns)
            await player1Connection.SendMessageAsync(gameStartForWhite);
        }

        if (player2Connection != null)
        {
            await player2Connection.SendMessageAsync(gameStartForBlack);
        }

        // Start the game
        game.Start();

        ServerLogger.LogGameStart(_logger, game.GameId, game.WhitePlayer.Username, game.BlackPlayer.Username);
    }

    private async void OnGameEnded(object? sender, GameEndedEventArgs e)
    {
        string winnerText = e.Winner.HasValue
            ? $"{e.Winner.Value}"
            : "Draw";

        ServerLogger.LogGameEnd(_logger, e.GameId, winnerText, e.EndReason.ToString());

        // Use player IDs from event args since game is already cleaned up
        await BroadcastToPlayersAsync(e.WhitePlayerId, e.BlackPlayerId, e.EndMessage);
    }

    /// <summary>
    /// Broadcasts a message directly to two players by their IDs
    /// Used when the game has already been cleaned up (e.g., after game end)
    /// </summary>
    private async Task BroadcastToPlayersAsync(string whitePlayerId, string blackPlayerId, NetworkMessage message)
    {
        var whiteConnection = GetConnectionByPlayerId(whitePlayerId);
        var blackConnection = GetConnectionByPlayerId(blackPlayerId);

        var tasks = new List<Task>();

        if (whiteConnection != null)
            tasks.Add(whiteConnection.SendMessageAsync(message));

        if (blackConnection != null)
            tasks.Add(blackConnection.SendMessageAsync(message));

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    #endregion

    #region Helper Methods

    private ClientConnection? GetConnectionByPlayerId(string playerId)
    {
        return _connections.Values.FirstOrDefault(c => c.PlayerId == playerId);
    }

    /// <summary>
    /// Disconnects any existing connections for a player, except the specified connection.
    /// This ensures a player can only be logged in on one client at a time.
    /// </summary>
    private async Task DisconnectExistingSessionsAsync(string playerId, string excludeConnectionId)
    {
        var existingConnections = _connections.Values
            .Where(c => c.PlayerId == playerId && c.ConnectionId != excludeConnectionId)
            .ToList();

        if (existingConnections.Count == 0)
            return;

        _logger.LogInformation("Disconnecting {Count} existing session(s) for player {PlayerId} due to new login",
            existingConnections.Count, playerId);

        foreach (var existingConnection in existingConnections)
        {
            try
            {
                // Notify the old client that they've been disconnected
                await existingConnection.SendMessageAsync(new ErrorMessage
                {
                    Code = "SESSION_REPLACED",
                    Message = "You have been disconnected because your account logged in from another location."
                });

                // Small delay to ensure message is sent before disconnecting
                await Task.Delay(100);

                // Disconnect without triggering game forfeit logic (player is reconnecting, not abandoning)
                _connections.TryRemove(existingConnection.ConnectionId, out _);
                existingConnection.Dispose();

                _logger.LogDebug("Disconnected existing session {ConnectionId} for player {PlayerId}",
                    existingConnection.ConnectionId, playerId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disconnecting existing session {ConnectionId} for player {PlayerId}",
                    existingConnection.ConnectionId, playerId);
            }
        }
    }

    private async Task BroadcastToGame(string gameId, NetworkMessage message)
    {
        var game = _gameManager.GetGame(gameId);
        if (game == null)
            return;

        var whiteConnection = GetConnectionByPlayerId(game.WhitePlayer.PlayerId);
        var blackConnection = GetConnectionByPlayerId(game.BlackPlayer.PlayerId);

        var tasks = new List<Task>();

        if (whiteConnection != null)
            tasks.Add(whiteConnection.SendMessageAsync(message));

        if (blackConnection != null)
            tasks.Add(blackConnection.SendMessageAsync(message));

        await Task.WhenAll(tasks);
    }

    private async Task DisconnectClientAsync(ClientConnection connection, string reason)
    {
        ServerLogger.LogPlayerDisconnected(_logger, connection.ConnectionId, connection.Username, reason);

        // Remove from matchmaking if in queue
        if (connection.PlayerId != null)
        {
            _matchmakingService.DequeuePlayer(connection.PlayerId);

            // Handle ongoing games
            var activeGameId = _gameManager.GetActiveGameForPlayer(connection.PlayerId);
            if (activeGameId != null)
            {
                // Give grace period before forfeiting
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.DisconnectionGracePeriodSeconds));

                    // Check if still disconnected
                    if (!_connections.Values.Any(c => c.PlayerId == connection.PlayerId && c.IsConnected))
                    {
                        _gameManager.HandleDisconnection(activeGameId, connection.PlayerId);
                    }
                });
            }
        }

        _connections.TryRemove(connection.ConnectionId, out _);
        connection.Dispose();
    }

    private async Task HeartbeatMonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isRunning)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.HeartbeatIntervalSeconds), ct);

                var timeout = TimeSpan.FromSeconds(_config.ConnectionTimeoutSeconds);
                var now = DateTime.UtcNow;

                var timedOutConnections = _connections.Values
                    .Where(c => now - c.LastActivity > timeout)
                    .ToList();

                foreach (var connection in timedOutConnections)
                {
                    _logger.LogWarning("Connection {ConnectionId} timed out (last activity: {LastActivity})",
                        connection.ConnectionId, connection.LastActivity);
                    await DisconnectClientAsync(connection, "Connection timeout");
                }

                // Clean up rate limiter
                _securityManager.CleanupRateLimits();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in heartbeat monitor");
            }
        }
    }

    #endregion

    public void Dispose()
    {
        _serverCts.Cancel();
        _serverCts.Dispose();
        _listener.Stop();

        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }

        _connections.Clear();
    }
}

/// <summary>
/// Server configuration settings
/// </summary>
public sealed class ServerConfiguration
{
    public int Port { get; set; } = 8787;
    public string BindAddress { get; set; } = "0.0.0.0";
    public int MaxConnections { get; set; } = 1000;
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int ConnectionTimeoutSeconds { get; set; } = 120;
    public int MaxRequestsPerMinute { get; set; } = 100;
    public int DisconnectionGracePeriodSeconds { get; set; } = 60;
}