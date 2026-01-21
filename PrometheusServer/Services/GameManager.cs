using System.Collections.Concurrent;
using ChessCore.Logic;
using ChessCore.Models;
using ChessCore.Network;
using PrometheusServer.Services;
using Microsoft.Extensions.Logging;
using PrometheusServer.Data;
using PrometheusServer.Logging;

namespace PrometheusServer.Services;

/// <summary>
/// Manages active chess games on the server.
/// This is the authoritative source for all game state - clients only display what the server tells them.
/// Supports both PostgreSQL-backed persistence and in-memory storage.
/// </summary>
public sealed class GameManager : IDisposable
{
    private readonly ILogger<GameManager> _logger;
    private readonly PlayerManager _playerManager;
    private readonly DatabaseService _databaseService;
    private readonly DatabaseConfiguration _dbConfig;
    private readonly MoveValidator _moveValidator;
    private readonly ConcurrentDictionary<string, GameSession> _activeGames = new();
    private readonly ConcurrentDictionary<string, string> _playerToGame = new();
    private readonly ConcurrentDictionary<string, Guid> _gameIdToDbId = new(); // Maps string GameId to DB Guid
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _timeoutMonitorTask;

    /// <summary>
    /// Event raised when a game ends
    /// </summary>
    public event EventHandler<GameEndedEventArgs>? GameEnded;

    /// <summary>
    /// Number of active games
    /// </summary>
    public int ActiveGameCount => _activeGames.Count;

    /// <summary>
    /// Gets whether the manager is using in-memory storage only.
    /// </summary>
    public bool IsInMemoryMode => _dbConfig.UseInMemory;

    /// <summary>
    /// Creates a new GameManager
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="playerManager">Player manager for rating updates</param>
    /// <param name="databaseService">Database service for persistence</param>
    /// <param name="dbConfig">Database configuration</param>
    public GameManager(
        ILogger<GameManager> logger,
        PlayerManager playerManager,
        DatabaseService databaseService,
        DatabaseConfiguration dbConfig)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _playerManager = playerManager ?? throw new ArgumentNullException(nameof(playerManager));
        _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
        _dbConfig = dbConfig ?? throw new ArgumentNullException(nameof(dbConfig));
        _moveValidator = new MoveValidator();

        // Start timeout monitoring
        _timeoutMonitorTask = Task.Run(() => TimeoutMonitorAsync(_cts.Token));

        var storageMode = _dbConfig.UseInMemory ? "in-memory" : "PostgreSQL";
        _logger.LogInformation("GameManager initialized with {StorageMode} storage", storageMode);
    }

    /// <summary>
    /// Creates a new game between two players
    /// </summary>
    /// <param name="whitePlayer">Information about the white player</param>
    /// <param name="blackPlayer">Information about the black player</param>
    /// <param name="initialTimeMs">Initial time for each player in milliseconds</param>
    /// <param name="incrementMs">Time increment per move in milliseconds</param>
    /// <param name="timeControl">Type of time control</param>
    /// <returns>The created game session</returns>
    public async Task<GameSession> CreateGameAsync(
        MatchedPlayer whitePlayer,
        MatchedPlayer blackPlayer,
        int initialTimeMs,
        int incrementMs,
        TimeControlType timeControl)
    {
        var whiteInfo = new PlayerInfo
        {
            PlayerId = whitePlayer.PlayerId,
            Username = whitePlayer.Username,
            Rating = whitePlayer.Rating
        };

        var blackInfo = new PlayerInfo
        {
            PlayerId = blackPlayer.PlayerId,
            Username = blackPlayer.Username,
            Rating = blackPlayer.Rating
        };

        var game = new GameSession(whiteInfo, blackInfo, initialTimeMs, incrementMs, timeControl);

        if (!_activeGames.TryAdd(game.GameId, game))
        {
            _logger.LogError("Failed to create game {GameId}", game.GameId);
            throw new InvalidOperationException("Failed to create game");
        }

        // Map players to game
        _playerToGame[whitePlayer.PlayerId] = game.GameId;
        _playerToGame[blackPlayer.PlayerId] = game.GameId;

        // Persist to database if not in-memory mode
        if (!_dbConfig.UseInMemory)
        {
            await PersistGameCreationAsync(game, whitePlayer, blackPlayer, timeControl);
        }

        _logger.LogDebug("Game {GameId} created: {White} (White) vs {Black} (Black)",
            game.GameId, whitePlayer.Username, blackPlayer.Username);

        return game;
    }

    /// <summary>
    /// Creates a new game between two players (synchronous wrapper for backward compatibility)
    /// </summary>
    public GameSession CreateGame(
        MatchedPlayer whitePlayer,
        MatchedPlayer blackPlayer,
        int initialTimeMs,
        int incrementMs,
        TimeControlType timeControl)
    {
        return CreateGameAsync(whitePlayer, blackPlayer, initialTimeMs, incrementMs, timeControl)
            .GetAwaiter().GetResult();
    }

    private async Task PersistGameCreationAsync(
        GameSession game,
        MatchedPlayer whitePlayer,
        MatchedPlayer blackPlayer,
        TimeControlType timeControl)
    {
        try
        {
            if (!Guid.TryParse(whitePlayer.PlayerId, out var whiteGuid) ||
                !Guid.TryParse(blackPlayer.PlayerId, out var blackGuid))
            {
                _logger.LogWarning("Invalid player ID format, skipping game persistence for {GameId}", game.GameId);
                return;
            }

            var timeControlStr = timeControl switch
            {
                TimeControlType.Bullet => "bullet",
                TimeControlType.Blitz => "blitz",
                TimeControlType.Rapid => "rapid",
                TimeControlType.Classical => "classical",
                _ => "rapid"
            };

            var gameRecord = await _databaseService.CreateGameAsync(
                whiteGuid,
                blackGuid,
                timeControlStr,
                game.WhiteTimeMs,
                game.TimeIncrementMs,
                whitePlayer.Rating,
                blackPlayer.Rating);

            if (gameRecord != null)
            {
                _gameIdToDbId[game.GameId] = gameRecord.GameId;
                _logger.LogDebug("Game {GameId} persisted to database as {DbId}", game.GameId, gameRecord.GameId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist game creation for {GameId}", game.GameId);
        }
    }

    /// <summary>
    /// Gets a game by its ID
    /// </summary>
    /// <param name="gameId">The game's ID</param>
    /// <returns>The game session or null if not found</returns>
    public GameSession? GetGame(string gameId)
    {
        _activeGames.TryGetValue(gameId, out var game);
        return game;
    }

    /// <summary>
    /// Gets the active game for a player
    /// </summary>
    /// <param name="playerId">The player's ID</param>
    /// <returns>The game ID or null if the player is not in a game</returns>
    public string? GetActiveGameForPlayer(string playerId)
    {
        _playerToGame.TryGetValue(playerId, out var gameId);
        return gameId;
    }

    /// <summary>
    /// Processes a move request from a player
    /// </summary>
    /// <param name="gameId">The game ID</param>
    /// <param name="playerId">The player making the move</param>
    /// <param name="fromNotation">Source position in algebraic notation (e.g., "e2")</param>
    /// <param name="toNotation">Destination position in algebraic notation (e.g., "e4")</param>
    /// <param name="promotion">Promotion piece type if applicable</param>
    /// <param name="expectedSequence">Expected move sequence number for synchronization</param>
    /// <returns>Move response message</returns>
    public async Task<MoveResponseMessage> ProcessMoveAsync(
        string gameId,
        string playerId,
        string fromNotation,
        string toNotation,
        string? promotion,
        int expectedSequence)
    {
        // Get the game
        var game = GetGame(gameId);
        if (game == null)
        {
            return CreateMoveResponse(gameId, false, MoveValidationResult.GameNotInProgress, "Game not found");
        }

        // Verify game is in progress
        if (game.Status != GameStatus.InProgress)
        {
            return CreateMoveResponse(gameId, false, MoveValidationResult.GameNotInProgress, "Game is not in progress");
        }

        // Verify player is in this game
        var playerColor = game.GetPlayerColor(playerId);
        if (playerColor == null)
        {
            return CreateMoveResponse(gameId, false, MoveValidationResult.InvalidPiece, "You are not in this game");
        }

        // Verify it's the player's turn
        if (game.Board.CurrentTurn != playerColor.Value)
        {
            return CreateMoveResponse(gameId, false, MoveValidationResult.NotYourTurn, "It's not your turn");
        }

        // Verify sequence number (detect out-of-order moves)
        if (expectedSequence != game.MoveSequence)
        {
            _logger.LogDebug("Move sequence mismatch for game {GameId}: expected {Expected}, got {Actual}",
                gameId, game.MoveSequence, expectedSequence);
            // We'll still process the move, but log the discrepancy
        }

        // Parse positions
        if (!Position.TryFromAlgebraic(fromNotation, out var from))
        {
            return CreateMoveResponse(gameId, false, MoveValidationResult.InvalidDestination, "Invalid source position");
        }

        if (!Position.TryFromAlgebraic(toNotation, out var to))
        {
            return CreateMoveResponse(gameId, false, MoveValidationResult.InvalidDestination, "Invalid destination position");
        }

        // Parse promotion type
        PieceType? promotionType = null;
        if (!string.IsNullOrEmpty(promotion))
        {
            promotionType = promotion.ToLower() switch
            {
                "q" or "queen" => PieceType.Queen,
                "r" or "rook" => PieceType.Rook,
                "b" or "bishop" => PieceType.Bishop,
                "n" or "knight" => PieceType.Knight,
                _ => null
            };
        }

        // Calculate time elapsed since last move
        var now = DateTime.UtcNow;
        var elapsedMs = game.LastMoveAt.HasValue
            ? (int)(now - game.LastMoveAt.Value).TotalMilliseconds
            : 0;

        // Check for timeout
        var currentTime = game.GetRemainingTime(playerColor.Value);
        if (currentTime - elapsedMs <= 0)
        {
            await HandleTimeoutAsync(gameId, playerColor.Value);
            return CreateMoveResponse(gameId, false, MoveValidationResult.GameNotInProgress, "Time expired");
        }

        // VALIDATE THE MOVE - This is the critical server-authoritative step
        var validationResult = _moveValidator.ValidateMove(game.Board, from, to, promotionType, playerColor.Value);

        if (!validationResult.IsValid)
        {
            _logger.LogDebug("Invalid move in game {GameId}: {Move} - {Message}",
                gameId, $"{fromNotation}{toNotation}", validationResult.Message);
            return CreateMoveResponse(gameId, false, validationResult.ValidationResult, validationResult.Message ?? "Invalid move");
        }

        // Move is valid - apply it to the game state
        var newBoard = _moveValidator.SimulateMove(game.Board, from, to, promotionType);

        // Update the game's board
        game.Board.LoadFromFen(newBoard.ToFen());

        // Update clock
        game.UpdateClock(playerColor.Value, elapsedMs);

        // Record the move
        var move = validationResult.Move;
        game.RecordMove(move);

        // Get remaining time for the player who just moved
        var remainingTimeMs = playerColor.Value == PieceColor.White ? game.WhiteTimeMs : game.BlackTimeMs;

        // Persist move to database
        if (!_dbConfig.UseInMemory)
        {
            await PersistMoveAsync(game, move, playerColor.Value, fromNotation, toNotation, promotion, remainingTimeMs, elapsedMs);
        }

        // Update game state
        game.UpdateGameState(validationResult.ResultsInCheck, validationResult.ResultsInCheckmate, validationResult.ResultsInStalemate);

        // Check for draw conditions
        if (game.IsFiftyMoveRule())
        {
            game.EndGame(GameStatus.Draw, GameEndReason.FiftyMoveRule);
            await OnGameEndedAsync(game);
        }
        else if (game.IsThreefoldRepetition())
        {
            game.EndGame(GameStatus.Draw, GameEndReason.ThreefoldRepetition);
            await OnGameEndedAsync(game);
        }
        else if (_moveValidator.IsInsufficientMaterial(game.Board))
        {
            game.EndGame(GameStatus.Draw, GameEndReason.InsufficientMaterial);
            await OnGameEndedAsync(game);
        }
        else if (validationResult.ResultsInCheckmate || validationResult.ResultsInStalemate)
        {
            await OnGameEndedAsync(game);
        }

        // Log the move with beautiful formatting
        var movingPlayer = playerColor.Value == PieceColor.White ? game.WhitePlayer.Username : game.BlackPlayer.Username;
        ServerLogger.LogMove(_logger, gameId, movingPlayer, move.ToCoordinateNotation(), game.Board.ToFen());

        // Create response
        var response = new MoveResponseMessage
        {
            Success = true,
            GameId = gameId,
            Move = move.ToCoordinateNotation(),
            NewFen = game.Board.ToFen(),
            ValidationResult = MoveValidationResult.Valid,
            IsCheck = validationResult.ResultsInCheck,
            IsCheckmate = validationResult.ResultsInCheckmate,
            IsStalemate = validationResult.ResultsInStalemate,
            WhiteTimeMs = game.WhiteTimeMs,
            BlackTimeMs = game.BlackTimeMs,
            MoveSequence = game.MoveSequence
        };

        return response;
    }

    private async Task PersistMoveAsync(
        GameSession game,
        Move move,
        PieceColor playerColor,
        string fromNotation,
        string toNotation,
        string? promotion,
        int remainingTimeMs,
        int moveTimeMs)
    {
        try
        {
            if (!_gameIdToDbId.TryGetValue(game.GameId, out var dbGameId))
            {
                _logger.LogDebug("No DB ID for game {GameId}, skipping move persistence", game.GameId);
                return;
            }

            var colorStr = playerColor == PieceColor.White ? "white" : "black";
            var moveNumber = (game.MoveSequence + 1) / 2; // Convert sequence to move number

            await _databaseService.RecordMoveAsync(
                dbGameId,
                moveNumber,
                colorStr,
                fromNotation.ToLowerInvariant(),
                toNotation.ToLowerInvariant(),
                promotion?.ToLowerInvariant(),
                move.ToSanNotation(),
                game.Board.ToFen(),
                remainingTimeMs,
                moveTimeMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist move for game {GameId}", game.GameId);
            // Don't throw - move was already applied to game state
        }
    }

    /// <summary>
    /// Handles a player resignation
    /// </summary>
    /// <param name="gameId">The game ID</param>
    /// <param name="playerId">The resigning player's ID</param>
    /// <returns>Game end message or null if game not found</returns>
    public async Task<GameEndMessage?> HandleResignationAsync(string gameId, string playerId)
    {
        var game = GetGame(gameId);
        if (game == null || game.Status != GameStatus.InProgress)
            return null;

        var playerColor = game.GetPlayerColor(playerId);
        if (playerColor == null)
            return null;

        game.Resign(playerColor.Value);

        var resigningPlayer = playerColor.Value == PieceColor.White ? game.WhitePlayer.Username : game.BlackPlayer.Username;
        _logger.LogInformation("Game {GameId}: {Player} ({Color}) resigned", gameId, resigningPlayer, playerColor);

        // Update ratings
        var (whiteRatingChange, blackRatingChange) = await UpdateRatingsAfterGameAsync(game);

        // Persist game completion
        if (!_dbConfig.UseInMemory)
        {
            await PersistGameCompletionAsync(game, whiteRatingChange, blackRatingChange);
        }

        var endMessage = CreateGameEndMessage(game, whiteRatingChange, blackRatingChange);
        await OnGameEndedAsync(game, endMessage);

        return endMessage;
    }

    /// <summary>
    /// Handles a player resignation (synchronous wrapper)
    /// </summary>
    public GameEndMessage? HandleResignation(string gameId, string playerId)
    {
        return HandleResignationAsync(gameId, playerId).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Handles a draw being accepted
    /// </summary>
    /// <param name="gameId">The game ID</param>
    /// <returns>Game end message or null if game not found</returns>
    public async Task<GameEndMessage?> HandleDrawAcceptedAsync(string gameId)
    {
        var game = GetGame(gameId);
        if (game == null || game.Status != GameStatus.InProgress)
            return null;

        game.AcceptDraw();

        _logger.LogInformation("Game {GameId}: Draw agreed between {White} and {Black}",
            gameId, game.WhitePlayer.Username, game.BlackPlayer.Username);

        // Update ratings
        var (whiteRatingChange, blackRatingChange) = await UpdateRatingsAfterGameAsync(game);

        // Persist game completion
        if (!_dbConfig.UseInMemory)
        {
            await PersistGameCompletionAsync(game, whiteRatingChange, blackRatingChange);
        }

        var endMessage = CreateGameEndMessage(game, whiteRatingChange, blackRatingChange);
        await OnGameEndedAsync(game, endMessage);

        return endMessage;
    }

    /// <summary>
    /// Handles a draw being accepted (synchronous wrapper)
    /// </summary>
    public GameEndMessage? HandleDrawAccepted(string gameId)
    {
        return HandleDrawAcceptedAsync(gameId).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Handles a player disconnection
    /// </summary>
    /// <param name="gameId">The game ID</param>
    /// <param name="playerId">The disconnected player's ID</param>
    public async Task HandleDisconnectionAsync(string gameId, string playerId)
    {
        var game = GetGame(gameId);
        if (game == null || game.Status != GameStatus.InProgress)
            return;

        var playerColor = game.GetPlayerColor(playerId);
        if (playerColor == null)
            return;

        game.HandleDisconnection(playerColor.Value);

        var disconnectedPlayer = playerColor.Value == PieceColor.White ? game.WhitePlayer.Username : game.BlackPlayer.Username;
        _logger.LogWarning("Game {GameId}: {Player} ({Color}) disconnected, game forfeited",
            gameId, disconnectedPlayer, playerColor);

        // Update ratings
        var (whiteRatingChange, blackRatingChange) = await UpdateRatingsAfterGameAsync(game);

        // Persist game completion
        if (!_dbConfig.UseInMemory)
        {
            await PersistGameCompletionAsync(game, whiteRatingChange, blackRatingChange);
        }

        await OnGameEndedAsync(game);
    }

    /// <summary>
    /// Handles a player disconnection (synchronous wrapper)
    /// </summary>
    public void HandleDisconnection(string gameId, string playerId)
    {
        HandleDisconnectionAsync(gameId, playerId).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Handles a timeout (flag fall)
    /// </summary>
    /// <param name="gameId">The game ID</param>
    /// <param name="timedOutColor">The color that ran out of time</param>
    private async Task HandleTimeoutAsync(string gameId, PieceColor timedOutColor)
    {
        var game = GetGame(gameId);
        if (game == null || game.Status != GameStatus.InProgress)
            return;

        game.HandleTimeout(timedOutColor);

        var timedOutPlayer = timedOutColor == PieceColor.White ? game.WhitePlayer.Username : game.BlackPlayer.Username;
        _logger.LogWarning("Game {GameId}: {Player} ({Color}) ran out of time ⏱",
            gameId, timedOutPlayer, timedOutColor);

        // Update ratings
        var (whiteRatingChange, blackRatingChange) = await UpdateRatingsAfterGameAsync(game);

        // Persist game completion
        if (!_dbConfig.UseInMemory)
        {
            await PersistGameCompletionAsync(game, whiteRatingChange, blackRatingChange);
        }

        await OnGameEndedAsync(game);
    }

    /// <summary>
    /// Updates player ratings after a game
    /// </summary>
    private async Task<(int WhiteRatingChange, int BlackRatingChange)> UpdateRatingsAfterGameAsync(GameSession game)
    {
        var whiteResult = game.Winner switch
        {
            PieceColor.White => 1.0,
            PieceColor.Black => 0.0,
            null when game.Status == GameStatus.Draw => 0.5,
            _ => 0.5
        };

        var blackResult = 1.0 - whiteResult;

        // Update white player and log rating change
        var (whiteNewRating, whiteRatingChange) = await _playerManager.UpdateRatingAsync(
            game.WhitePlayer.PlayerId, game.BlackPlayer.Rating, whiteResult);
        await _playerManager.UpdateGameStatsAsync(game.WhitePlayer.PlayerId, whiteResult);
        if (whiteRatingChange != 0)
        {
            ServerLogger.LogRatingChange(_logger, game.WhitePlayer.Username, game.WhitePlayer.Rating, whiteNewRating);
        }

        // Update black player and log rating change
        var (blackNewRating, blackRatingChange) = await _playerManager.UpdateRatingAsync(
            game.BlackPlayer.PlayerId, game.WhitePlayer.Rating, blackResult);
        await _playerManager.UpdateGameStatsAsync(game.BlackPlayer.PlayerId, blackResult);
        if (blackRatingChange != 0)
        {
            ServerLogger.LogRatingChange(_logger, game.BlackPlayer.Username, game.BlackPlayer.Rating, blackNewRating);
        }

        return (whiteRatingChange, blackRatingChange);
    }

    private async Task PersistGameCompletionAsync(GameSession game, int whiteRatingChange, int blackRatingChange)
    {
        try
        {
            if (!_gameIdToDbId.TryGetValue(game.GameId, out var dbGameId))
            {
                _logger.LogDebug("No DB ID for game {GameId}, skipping completion persistence", game.GameId);
                return;
            }

            var result = game.Status switch
            {
                GameStatus.WhiteWon => "white_win",
                GameStatus.BlackWon => "black_win",
                GameStatus.Draw => "draw",
                _ => "draw"
            };

            var endReason = game.EndReason switch
            {
                GameEndReason.Checkmate => "checkmate",
                GameEndReason.Resignation => "resignation",
                GameEndReason.Timeout => "timeout",
                GameEndReason.Disconnection => "disconnection",
                GameEndReason.DrawAgreement => "agreement",
                GameEndReason.Stalemate => "stalemate",
                GameEndReason.InsufficientMaterial => "insufficient_material",
                GameEndReason.FiftyMoveRule => "fifty_move_rule",
                GameEndReason.ThreefoldRepetition => "threefold_repetition",
                _ => "other"
            };

            // Generate PGN (simplified version)
            var pgn = GeneratePgn(game);

            await _databaseService.CompleteGameAsync(
                dbGameId,
                result,
                endReason,
                pgn,
                game.Board.ToFen(),
                whiteRatingChange,
                blackRatingChange);

            _logger.LogDebug("Game {GameId} completion persisted: {Result} ({EndReason})",
                game.GameId, result, endReason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist game completion for {GameId}", game.GameId);
            // Don't throw - game has already ended
        }
    }

    private string GeneratePgn(GameSession game)
    {
        // Generate a simple PGN representation
        var moves = game.MoveHistory;
        var pgnMoves = new System.Text.StringBuilder();

        var moveNumber = 1;
        for (int i = 0; i < moves.Count; i++)
        {
            if (i % 2 == 0)
            {
                pgnMoves.Append($"{moveNumber}. ");
                moveNumber++;
            }
            pgnMoves.Append(moves[i].ToSanNotation());
            pgnMoves.Append(' ');
        }

        // Add result
        var result = game.Status switch
        {
            GameStatus.WhiteWon => "1-0",
            GameStatus.BlackWon => "0-1",
            GameStatus.Draw => "1/2-1/2",
            _ => "*"
        };

        return $"[White \"{game.WhitePlayer.Username}\"]\n" +
               $"[Black \"{game.BlackPlayer.Username}\"]\n" +
               $"[Result \"{result}\"]\n" +
               $"[WhiteElo \"{game.WhitePlayer.Rating}\"]\n" +
               $"[BlackElo \"{game.BlackPlayer.Rating}\"]\n\n" +
               $"{pgnMoves}{result}";
    }

    /// <summary>
    /// Creates a game end message
    /// </summary>
    private GameEndMessage CreateGameEndMessage(GameSession game, int whiteRatingChange = 0, int blackRatingChange = 0)
    {
        return new GameEndMessage
        {
            GameId = game.GameId,
            Status = game.Status,
            Reason = game.EndReason ?? GameEndReason.Checkmate,
            Winner = game.Winner,
            FinalFen = game.Board.ToFen(),
            RatingChange = 0, // Per-player, set by caller
            NewRating = 0,    // Per-player, set by caller
            WhiteRatingChange = whiteRatingChange,
            BlackRatingChange = blackRatingChange
        };
    }

    /// <summary>
    /// Creates a move response message
    /// </summary>
    private static MoveResponseMessage CreateMoveResponse(
        string gameId,
        bool success,
        MoveValidationResult result,
        string? message)
    {
        return new MoveResponseMessage
        {
            Success = success,
            GameId = gameId,
            ValidationResult = result,
            Message = message
        };
    }

    /// <summary>
    /// Raises the GameEnded event
    /// </summary>
    private async Task OnGameEndedAsync(GameSession game, GameEndMessage? endMessage = null)
    {
        // Capture player IDs BEFORE cleanup since we need them for broadcasting
        var whitePlayerId = game.WhitePlayer.PlayerId;
        var blackPlayerId = game.BlackPlayer.PlayerId;

        if (endMessage == null)
        {
            var (whiteRatingChange, blackRatingChange) = await UpdateRatingsAfterGameAsync(game);

            // Persist game completion
            if (!_dbConfig.UseInMemory)
            {
                await PersistGameCompletionAsync(game, whiteRatingChange, blackRatingChange);
            }

            endMessage = CreateGameEndMessage(game, whiteRatingChange, blackRatingChange);
        }

        // Clean up game references
        CleanupGame(game.GameId);

        // Fire event with all needed info (including player IDs for broadcasting)
        GameEnded?.Invoke(this, new GameEndedEventArgs
        {
            GameId = game.GameId,
            Status = game.Status,
            EndReason = game.EndReason ?? GameEndReason.Checkmate,
            Winner = game.Winner,
            EndMessage = endMessage,
            WhitePlayerId = whitePlayerId,
            BlackPlayerId = blackPlayerId
        });
    }

    /// <summary>
    /// Cleans up a finished game
    /// </summary>
    private void CleanupGame(string gameId)
    {
        if (_activeGames.TryRemove(gameId, out var game))
        {
            _playerToGame.TryRemove(game.WhitePlayer.PlayerId, out _);
            _playerToGame.TryRemove(game.BlackPlayer.PlayerId, out _);
            _gameIdToDbId.TryRemove(gameId, out _);

            _logger.LogInformation("Game {GameId} cleaned up", gameId);
        }
    }

    /// <summary>
    /// Monitors games for timeouts
    /// </summary>
    private async Task TimeoutMonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct); // Check every second

                var now = DateTime.UtcNow;

                foreach (var game in _activeGames.Values.ToList())
                {
                    if (game.Status != GameStatus.InProgress || game.LastMoveAt == null)
                        continue;

                    var elapsedMs = (int)(now - game.LastMoveAt.Value).TotalMilliseconds;
                    var currentTurn = game.Board.CurrentTurn;
                    var remainingTime = game.GetRemainingTime(currentTurn);

                    if (remainingTime - elapsedMs <= 0)
                    {
                        await HandleTimeoutAsync(game.GameId, currentTurn);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in timeout monitor");
            }
        }
    }

    /// <summary>
    /// Gets a snapshot of all active games (for monitoring)
    /// </summary>
    public IReadOnlyCollection<GameSession> GetActiveGamesSnapshot()
    {
        return _activeGames.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets a player's game history from the database
    /// </summary>
    public async Task<List<GameRecord>> GetPlayerGameHistoryAsync(string playerId, int limit = 50, int offset = 0)
    {
        if (_dbConfig.UseInMemory)
        {
            return new List<GameRecord>();
        }

        try
        {
            if (!Guid.TryParse(playerId, out var playerGuid))
            {
                return new List<GameRecord>();
            }

            return await _databaseService.GetPlayerGamesAsync(playerGuid, limit, offset);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get game history for player {PlayerId}", playerId);
            return new List<GameRecord>();
        }
    }

    /// <summary>
    /// Gets the moves for a specific game from the database
    /// </summary>
    public async Task<List<GameMoveRecord>> GetGameMovesAsync(string gameId)
    {
        if (_dbConfig.UseInMemory)
        {
            return new List<GameMoveRecord>();
        }

        try
        {
            if (!_gameIdToDbId.TryGetValue(gameId, out var dbGameId))
            {
                // Try parsing as GUID directly (for historical games)
                if (!Guid.TryParse(gameId, out dbGameId))
                {
                    return new List<GameMoveRecord>();
                }
            }

            return await _databaseService.GetGameMovesAsync(dbGameId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get moves for game {GameId}", gameId);
            return new List<GameMoveRecord>();
        }
    }

    /// <summary>
    /// Disposes of the GameManager
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            _timeoutMonitorTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Expected when cancellation occurs
        }

        _cts.Dispose();

        _logger.LogInformation("GameManager disposed");
    }
}

/// <summary>
/// Event arguments for when a game ends
/// </summary>
public sealed class GameEndedEventArgs : EventArgs
{
    public required string GameId { get; init; }
    public required GameStatus Status { get; init; }
    public required GameEndReason EndReason { get; init; }
    public PieceColor? Winner { get; init; }
    public required GameEndMessage EndMessage { get; init; }

    /// <summary>
    /// White player's ID (captured before game cleanup for broadcasting)
    /// </summary>
    public required string WhitePlayerId { get; init; }

    /// <summary>
    /// Black player's ID (captured before game cleanup for broadcasting)
    /// </summary>
    public required string BlackPlayerId { get; init; }
}
