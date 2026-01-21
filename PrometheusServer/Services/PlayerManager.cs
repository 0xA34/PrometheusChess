using System.Collections.Concurrent;
using ChessCore.Security;
using Microsoft.Extensions.Logging;
using PrometheusServer.Data;

namespace PrometheusServer.Services;

/// <summary>
/// Manages player data, authentication, and sessions.
/// Supports both PostgreSQL-backed storage and in-memory storage for development/testing.
/// </summary>
public sealed class PlayerManager
{
    private readonly ILogger<PlayerManager> _logger;
    private readonly DatabaseService? _databaseService;
    private readonly DatabaseConfiguration _dbConfig;

    // In-memory storage (used when UseInMemory = true)
    private readonly ConcurrentDictionary<string, PlayerData> _players = new();
    private readonly ConcurrentDictionary<string, PlayerData> _playersByUsername = new();
    private readonly ConcurrentDictionary<string, string> _emailToPlayerId = new();

    private readonly int _defaultRating;
    private readonly int _minRating;
    private readonly int _maxRating;
    private readonly int _kFactor;

    /// <summary>
    /// Gets whether the manager is using in-memory storage.
    /// </summary>
    public bool IsInMemoryMode => _dbConfig.UseInMemory;

    /// <summary>
    /// Creates a new PlayerManager
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="databaseService">Database service for persistent storage</param>
    /// <param name="dbConfig">Database configuration</param>
    /// <param name="defaultRating">Default rating for new players</param>
    /// <param name="kFactor">K-factor for ELO calculations</param>
    /// <param name="minRating">Minimum possible rating</param>
    /// <param name="maxRating">Maximum possible rating</param>
    public PlayerManager(
        ILogger<PlayerManager> logger,
        DatabaseService databaseService,
        DatabaseConfiguration dbConfig,
        int defaultRating = 1200,
        int kFactor = 32,
        int minRating = 100,
        int maxRating = 3000)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
        _dbConfig = dbConfig ?? throw new ArgumentNullException(nameof(dbConfig));
        _defaultRating = defaultRating;
        _kFactor = kFactor;
        _minRating = minRating;
        _maxRating = maxRating;

        var storageMode = _dbConfig.UseInMemory ? "in-memory" : "PostgreSQL";
        _logger.LogDebug("PlayerManager initialized with {StorageMode} storage, default rating {Rating}, K-factor {KFactor}",
            storageMode, _defaultRating, _kFactor);
    }

    #region Registration & Authentication

    /// <summary>
    /// Registers a new player
    /// </summary>
    /// <param name="username">Desired username</param>
    /// <param name="email">Email address</param>
    /// <param name="passwordHash">Client-side hashed password</param>
    /// <returns>Registration result</returns>
    public async Task<RegistrationResult> RegisterAsync(string username, string email, string passwordHash)
    {
        // Normalize inputs
        username = username.Trim();
        email = email.Trim().ToLowerInvariant();

        // Hash the password again server-side for storage
        var serverPasswordHash = SecurityManager.HashPassword(passwordHash);

        if (_dbConfig.UseInMemory)
        {
            return await RegisterInMemoryAsync(username, email, serverPasswordHash);
        }

        return await RegisterDatabaseAsync(username, email, serverPasswordHash);
    }

    private Task<RegistrationResult> RegisterInMemoryAsync(string username, string email, string serverPasswordHash)
    {
        // Check if username is taken
        if (_playersByUsername.ContainsKey(username.ToLowerInvariant()))
        {
            return Task.FromResult(new RegistrationResult
            {
                Success = false,
                ErrorCode = "USERNAME_TAKEN",
                Message = "This username is already taken"
            });
        }

        // Check if email is taken
        if (_emailToPlayerId.ContainsKey(email))
        {
            return Task.FromResult(new RegistrationResult
            {
                Success = false,
                ErrorCode = "EMAIL_TAKEN",
                Message = "An account with this email already exists"
            });
        }

        // Create new player
        var playerId = SecurityManager.GeneratePlayerId();

        var player = new PlayerData
        {
            PlayerId = playerId,
            Username = username,
            Email = email,
            PasswordHash = serverPasswordHash,
            Rating = _defaultRating,
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = null,
            GamesPlayed = 0,
            Wins = 0,
            Losses = 0,
            Draws = 0
        };

        // Add to dictionaries
        if (!_players.TryAdd(playerId, player))
        {
            return Task.FromResult(new RegistrationResult
            {
                Success = false,
                ErrorCode = "REGISTRATION_FAILED",
                Message = "Failed to create account. Please try again."
            });
        }

        _playersByUsername.TryAdd(username.ToLowerInvariant(), player);
        _emailToPlayerId.TryAdd(email, playerId);

        _logger.LogDebug("New player created (in-memory): {Username} ({PlayerId})", username, playerId);

        return Task.FromResult(new RegistrationResult
        {
            Success = true,
            PlayerId = playerId,
            Message = "Registration successful"
        });
    }

    private async Task<RegistrationResult> RegisterDatabaseAsync(string username, string email, string serverPasswordHash)
    {
        try
        {
            // Check if username is available
            if (!await _databaseService!.IsUsernameAvailableAsync(username))
            {
                return new RegistrationResult
                {
                    Success = false,
                    ErrorCode = "USERNAME_TAKEN",
                    Message = "This username is already taken"
                };
            }

            // Check if email is available
            if (!await _databaseService.IsEmailAvailableAsync(email))
            {
                return new RegistrationResult
                {
                    Success = false,
                    ErrorCode = "EMAIL_TAKEN",
                    Message = "An account with this email already exists"
                };
            }

            // Create the player in the database
            var playerRecord = await _databaseService.CreatePlayerAsync(
                username, email, serverPasswordHash, _defaultRating);

            if (playerRecord == null)
            {
                return new RegistrationResult
                {
                    Success = false,
                    ErrorCode = "REGISTRATION_FAILED",
                    Message = "Failed to create account. Please try again."
                };
            }

            _logger.LogDebug("New player created (database): {Username} ({PlayerId})",
                username, playerRecord.PlayerId);

            return new RegistrationResult
            {
                Success = true,
                PlayerId = playerRecord.PlayerId.ToString(),
                Message = "Registration successful"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error during registration for user: {Username}", username);
            return new RegistrationResult
            {
                Success = false,
                ErrorCode = "DATABASE_ERROR",
                Message = "A database error occurred. Please try again later."
            };
        }
    }

    /// <summary>
    /// Authenticates a player with username and password
    /// </summary>
    /// <param name="username">Username</param>
    /// <param name="passwordHash">Client-side hashed password</param>
    /// <returns>Player data if authenticated, null otherwise</returns>
    public async Task<PlayerData?> AuthenticateAsync(string username, string passwordHash)
    {
        username = username.Trim();

        if (_dbConfig.UseInMemory)
        {
            return await AuthenticateInMemoryAsync(username, passwordHash);
        }

        return await AuthenticateDatabaseAsync(username, passwordHash);
    }

    private Task<PlayerData?> AuthenticateInMemoryAsync(string username, string passwordHash)
    {
        if (!_playersByUsername.TryGetValue(username.ToLowerInvariant(), out var player))
        {
            _logger.LogWarning("Login attempt for unknown user: {Username}", username);
            return Task.FromResult<PlayerData?>(null);
        }

        // Verify password
        if (!SecurityManager.VerifyPassword(passwordHash, player.PasswordHash))
        {
            _logger.LogWarning("Failed login attempt for user: {Username} (invalid password)", username);
            return Task.FromResult<PlayerData?>(null);
        }

        // Check if banned
        if (player.IsBanned)
        {
            _logger.LogWarning("Login attempt by banned user: {Username}", username);
            return Task.FromResult<PlayerData?>(null);
        }

        // Update last login time
        player.LastLoginAt = DateTime.UtcNow;

        _logger.LogDebug("Player authenticated (in-memory): {Username} (Rating: {Rating})",
            player.Username, player.Rating);

        return Task.FromResult<PlayerData?>(player);
    }

    private async Task<PlayerData?> AuthenticateDatabaseAsync(string username, string passwordHash)
    {
        try
        {
            var playerRecord = await _databaseService!.GetPlayerByUsernameAsync(username);

            if (playerRecord == null)
            {
                _logger.LogWarning("Login attempt for unknown user: {Username}", username);
                return null;
            }

            // Verify password
            if (playerRecord.PasswordHash == null ||
                !SecurityManager.VerifyPassword(passwordHash, playerRecord.PasswordHash))
            {
                _logger.LogWarning("Failed login attempt for user: {Username} (invalid password)", username);
                return null;
            }

            // Check if banned
            if (playerRecord.IsBanned)
            {
                _logger.LogWarning("Login attempt by banned user: {Username}, Reason: {Reason}",
                    username, playerRecord.BanReason ?? "No reason provided");
                return null;
            }

            // Update last login time
            await _databaseService.UpdateLastLoginAsync(playerRecord.PlayerId);

            var playerData = MapRecordToPlayerData(playerRecord);
            playerData.LastLoginAt = DateTime.UtcNow;

            _logger.LogDebug("Player authenticated (database): {Username} (Rating: {Rating})",
                playerData.Username, playerData.Rating);

            return playerData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error during authentication for user: {Username}", username);
            return null;
        }
    }

    #endregion

    #region Player Retrieval

    /// <summary>
    /// Gets a player by their ID
    /// </summary>
    /// <param name="playerId">The player's ID</param>
    /// <returns>Player data or null if not found</returns>
    public PlayerData? GetPlayerById(string playerId)
    {
        if (_dbConfig.UseInMemory)
        {
            _players.TryGetValue(playerId, out var player);
            return player;
        }

        // For database mode, use async version synchronously (not ideal but maintains API compatibility)
        return GetPlayerByIdAsync(playerId).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets a player by their ID asynchronously
    /// </summary>
    /// <param name="playerId">The player's ID</param>
    /// <returns>Player data or null if not found</returns>
    public async Task<PlayerData?> GetPlayerByIdAsync(string playerId)
    {
        if (_dbConfig.UseInMemory)
        {
            _players.TryGetValue(playerId, out var player);
            return player;
        }

        try
        {
            if (!Guid.TryParse(playerId, out var playerGuid))
            {
                _logger.LogWarning("Invalid player ID format: {PlayerId}", playerId);
                return null;
            }

            var playerRecord = await _databaseService!.GetPlayerByIdAsync(playerGuid);
            return playerRecord != null ? MapRecordToPlayerData(playerRecord) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error getting player by ID: {PlayerId}", playerId);
            return null;
        }
    }

    /// <summary>
    /// Gets a player by their username
    /// </summary>
    /// <param name="username">The player's username</param>
    /// <returns>Player data or null if not found</returns>
    public PlayerData? GetPlayerByUsername(string username)
    {
        if (_dbConfig.UseInMemory)
        {
            _playersByUsername.TryGetValue(username.ToLowerInvariant(), out var player);
            return player;
        }

        return GetPlayerByUsernameAsync(username).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets a player by their username asynchronously
    /// </summary>
    /// <param name="username">The player's username</param>
    /// <returns>Player data or null if not found</returns>
    public async Task<PlayerData?> GetPlayerByUsernameAsync(string username)
    {
        if (_dbConfig.UseInMemory)
        {
            _playersByUsername.TryGetValue(username.ToLowerInvariant(), out var player);
            return player;
        }

        try
        {
            var playerRecord = await _databaseService!.GetPlayerByUsernameAsync(username);
            return playerRecord != null ? MapRecordToPlayerData(playerRecord) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error getting player by username: {Username}", username);
            return null;
        }
    }

    #endregion

    #region Rating & Statistics

    /// <summary>
    /// Updates a player's rating after a game
    /// </summary>
    /// <param name="playerId">The player's ID</param>
    /// <param name="opponentRating">The opponent's rating</param>
    /// <param name="result">Game result (1 = win, 0.5 = draw, 0 = loss)</param>
    /// <returns>The new rating and rating change</returns>
    public (int NewRating, int RatingChange) UpdateRating(string playerId, int opponentRating, double result)
    {
        if (_dbConfig.UseInMemory)
        {
            return UpdateRatingInMemory(playerId, opponentRating, result);
        }

        return UpdateRatingDatabaseAsync(playerId, opponentRating, result).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Updates a player's rating after a game asynchronously
    /// </summary>
    public async Task<(int NewRating, int RatingChange)> UpdateRatingAsync(string playerId, int opponentRating, double result)
    {
        if (_dbConfig.UseInMemory)
        {
            return UpdateRatingInMemory(playerId, opponentRating, result);
        }

        return await UpdateRatingDatabaseAsync(playerId, opponentRating, result);
    }

    private (int NewRating, int RatingChange) UpdateRatingInMemory(string playerId, int opponentRating, double result)
    {
        if (!_players.TryGetValue(playerId, out var player))
        {
            return (0, 0);
        }

        var oldRating = player.Rating;
        var expectedScore = CalculateExpectedScore(player.Rating, opponentRating);
        var ratingChange = (int)Math.Round(_kFactor * (result - expectedScore));

        var newRating = Math.Clamp(player.Rating + ratingChange, _minRating, _maxRating);
        player.Rating = newRating;

        _logger.LogDebug("Rating calculated for {Username}: {OldRating} -> {NewRating} ({Change:+#;-#;0})",
            player.Username, oldRating, newRating, ratingChange);

        return (newRating, ratingChange);
    }

    private async Task<(int NewRating, int RatingChange)> UpdateRatingDatabaseAsync(string playerId, int opponentRating, double result)
    {
        try
        {
            if (!Guid.TryParse(playerId, out var playerGuid))
            {
                _logger.LogWarning("Invalid player ID format for rating update: {PlayerId}", playerId);
                return (0, 0);
            }

            var playerRecord = await _databaseService!.GetPlayerByIdAsync(playerGuid);
            if (playerRecord == null)
            {
                return (0, 0);
            }

            var oldRating = playerRecord.Rating;
            var expectedScore = CalculateExpectedScore(playerRecord.Rating, opponentRating);
            var ratingChange = (int)Math.Round(_kFactor * (result - expectedScore));

            var newRating = Math.Clamp(playerRecord.Rating + ratingChange, _minRating, _maxRating);

            await _databaseService.UpdatePlayerRatingAsync(playerGuid, newRating, ratingChange);

            _logger.LogDebug("Rating calculated for {Username}: {OldRating} -> {NewRating} ({Change:+#;-#;0})",
                playerRecord.Username, oldRating, newRating, ratingChange);

            return (newRating, ratingChange);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error updating rating for player: {PlayerId}", playerId);
            return (0, 0);
        }
    }

    /// <summary>
    /// Calculates the expected score using ELO formula
    /// </summary>
    private static double CalculateExpectedScore(int playerRating, int opponentRating)
    {
        return 1.0 / (1.0 + Math.Pow(10, (opponentRating - playerRating) / 400.0));
    }

    /// <summary>
    /// Updates player statistics after a game
    /// </summary>
    /// <param name="playerId">The player's ID</param>
    /// <param name="result">Game result (1 = win, 0.5 = draw, 0 = loss)</param>
    public void UpdateGameStats(string playerId, double result)
    {
        if (_dbConfig.UseInMemory)
        {
            UpdateGameStatsInMemory(playerId, result);
            return;
        }

        UpdateGameStatsDatabaseAsync(playerId, result).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Updates player statistics after a game asynchronously
    /// </summary>
    public async Task UpdateGameStatsAsync(string playerId, double result)
    {
        if (_dbConfig.UseInMemory)
        {
            UpdateGameStatsInMemory(playerId, result);
            return;
        }

        await UpdateGameStatsDatabaseAsync(playerId, result);
    }

    private void UpdateGameStatsInMemory(string playerId, double result)
    {
        if (!_players.TryGetValue(playerId, out var player))
        {
            return;
        }

        player.GamesPlayed++;

        if (result > 0.9)
            player.Wins++;
        else if (result < 0.1)
            player.Losses++;
        else
            player.Draws++;
    }

    private async Task UpdateGameStatsDatabaseAsync(string playerId, double result)
    {
        try
        {
            if (!Guid.TryParse(playerId, out var playerGuid))
            {
                _logger.LogWarning("Invalid player ID format for stats update: {PlayerId}", playerId);
                return;
            }

            await _databaseService!.UpdateGameStatsAsync(playerGuid, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error updating game stats for player: {PlayerId}", playerId);
        }
    }

    /// <summary>
    /// Gets the leaderboard
    /// </summary>
    /// <param name="top">Number of top players to return</param>
    /// <returns>List of players sorted by rating</returns>
    public List<PlayerData> GetLeaderboard(int top = 100)
    {
        if (_dbConfig.UseInMemory)
        {
            return _players.Values
                .Where(p => !p.IsBanned)
                .OrderByDescending(p => p.Rating)
                .ThenByDescending(p => p.Wins)
                .Take(top)
                .ToList();
        }

        return GetLeaderboardAsync(top).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets the leaderboard asynchronously
    /// </summary>
    public async Task<List<PlayerData>> GetLeaderboardAsync(int top = 100)
    {
        if (_dbConfig.UseInMemory)
        {
            return _players.Values
                .Where(p => !p.IsBanned)
                .OrderByDescending(p => p.Rating)
                .ThenByDescending(p => p.Wins)
                .Take(top)
                .ToList();
        }

        try
        {
            var records = await _databaseService!.GetLeaderboardAsync(top);
            return records.Select(MapRecordToPlayerData).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error getting leaderboard");
            return new List<PlayerData>();
        }
    }

    /// <summary>
    /// Gets player statistics
    /// </summary>
    /// <param name="playerId">The player's ID</param>
    /// <returns>Player statistics or null if not found</returns>
    public PlayerStatistics? GetPlayerStatistics(string playerId)
    {
        if (_dbConfig.UseInMemory)
        {
            return GetPlayerStatisticsInMemory(playerId);
        }

        return GetPlayerStatisticsAsync(playerId).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets player statistics asynchronously
    /// </summary>
    public async Task<PlayerStatistics?> GetPlayerStatisticsAsync(string playerId)
    {
        if (_dbConfig.UseInMemory)
        {
            return GetPlayerStatisticsInMemory(playerId);
        }

        try
        {
            if (!Guid.TryParse(playerId, out var playerGuid))
            {
                return null;
            }

            var playerRecord = await _databaseService!.GetPlayerByIdAsync(playerGuid);
            if (playerRecord == null)
            {
                return null;
            }

            var rank = await _databaseService.GetPlayerRankAsync(playerGuid);

            return new PlayerStatistics
            {
                PlayerId = playerRecord.PlayerId.ToString(),
                Username = playerRecord.Username,
                Rating = playerRecord.Rating,
                GamesPlayed = playerRecord.GamesPlayed,
                Wins = playerRecord.GamesWon,
                Losses = playerRecord.GamesLost,
                Draws = playerRecord.GamesDrawn,
                WinRate = playerRecord.GamesPlayed > 0
                    ? (double)playerRecord.GamesWon / playerRecord.GamesPlayed * 100
                    : 0,
                Rank = rank,
                MemberSince = playerRecord.CreatedAt,
                LastPlayed = playerRecord.LastLoginAt
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error getting player statistics: {PlayerId}", playerId);
            return null;
        }
    }

    private PlayerStatistics? GetPlayerStatisticsInMemory(string playerId)
    {
        if (!_players.TryGetValue(playerId, out var player))
        {
            return null;
        }

        return new PlayerStatistics
        {
            PlayerId = player.PlayerId,
            Username = player.Username,
            Rating = player.Rating,
            GamesPlayed = player.GamesPlayed,
            Wins = player.Wins,
            Losses = player.Losses,
            Draws = player.Draws,
            WinRate = player.GamesPlayed > 0
                ? (double)player.Wins / player.GamesPlayed * 100
                : 0,
            Rank = GetPlayerRank(playerId),
            MemberSince = player.CreatedAt,
            LastPlayed = player.LastLoginAt
        };
    }

    /// <summary>
    /// Gets a player's rank on the leaderboard
    /// </summary>
    /// <param name="playerId">The player's ID</param>
    /// <returns>Rank (1-based) or -1 if not found</returns>
    public int GetPlayerRank(string playerId)
    {
        if (_dbConfig.UseInMemory)
        {
            if (!_players.TryGetValue(playerId, out var player))
            {
                return -1;
            }

            return _players.Values.Count(p => p.Rating > player.Rating && !p.IsBanned) + 1;
        }

        return GetPlayerRankAsync(playerId).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets a player's rank on the leaderboard asynchronously
    /// </summary>
    public async Task<int> GetPlayerRankAsync(string playerId)
    {
        if (_dbConfig.UseInMemory)
        {
            if (!_players.TryGetValue(playerId, out var player))
            {
                return -1;
            }

            return _players.Values.Count(p => p.Rating > player.Rating && !p.IsBanned) + 1;
        }

        try
        {
            if (!Guid.TryParse(playerId, out var playerGuid))
            {
                return -1;
            }

            return await _databaseService!.GetPlayerRankAsync(playerGuid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error getting player rank: {PlayerId}", playerId);
            return -1;
        }
    }

    #endregion

    #region Availability Checks

    /// <summary>
    /// Checks if a username is available
    /// </summary>
    /// <param name="username">The username to check</param>
    /// <returns>True if available</returns>
    public bool IsUsernameAvailable(string username)
    {
        if (_dbConfig.UseInMemory)
        {
            return !_playersByUsername.ContainsKey(username.Trim().ToLowerInvariant());
        }

        return IsUsernameAvailableAsync(username).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Checks if a username is available asynchronously
    /// </summary>
    public async Task<bool> IsUsernameAvailableAsync(string username)
    {
        if (_dbConfig.UseInMemory)
        {
            return !_playersByUsername.ContainsKey(username.Trim().ToLowerInvariant());
        }

        try
        {
            return await _databaseService!.IsUsernameAvailableAsync(username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error checking username availability: {Username}", username);
            return false;
        }
    }

    /// <summary>
    /// Checks if an email is available
    /// </summary>
    /// <param name="email">The email to check</param>
    /// <returns>True if available</returns>
    public bool IsEmailAvailable(string email)
    {
        if (_dbConfig.UseInMemory)
        {
            return !_emailToPlayerId.ContainsKey(email.Trim().ToLowerInvariant());
        }

        return IsEmailAvailableAsync(email).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Checks if an email is available asynchronously
    /// </summary>
    public async Task<bool> IsEmailAvailableAsync(string email)
    {
        if (_dbConfig.UseInMemory)
        {
            return !_emailToPlayerId.ContainsKey(email.Trim().ToLowerInvariant());
        }

        try
        {
            return await _databaseService!.IsEmailAvailableAsync(email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error checking email availability: {Email}", email);
            return false;
        }
    }

    /// <summary>
    /// Gets the total number of registered players
    /// </summary>
    public int TotalPlayers
    {
        get
        {
            if (_dbConfig.UseInMemory)
            {
                return _players.Count;
            }

            return GetTotalPlayersAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Gets the total number of registered players asynchronously
    /// </summary>
    public async Task<int> GetTotalPlayersAsync()
    {
        if (_dbConfig.UseInMemory)
        {
            return _players.Count;
        }

        try
        {
            return await _databaseService!.GetTotalPlayerCountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error getting total player count");
            return 0;
        }
    }

    #endregion

    #region Password Management

    /// <summary>
    /// Updates a player's password
    /// </summary>
    /// <param name="playerId">The player's ID</param>
    /// <param name="oldPasswordHash">Current password (client-hashed)</param>
    /// <param name="newPasswordHash">New password (client-hashed)</param>
    /// <returns>True if password was updated</returns>
    public bool UpdatePassword(string playerId, string oldPasswordHash, string newPasswordHash)
    {
        if (_dbConfig.UseInMemory)
        {
            return UpdatePasswordInMemory(playerId, oldPasswordHash, newPasswordHash);
        }

        return UpdatePasswordAsync(playerId, oldPasswordHash, newPasswordHash).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Updates a player's password asynchronously
    /// </summary>
    public async Task<bool> UpdatePasswordAsync(string playerId, string oldPasswordHash, string newPasswordHash)
    {
        if (_dbConfig.UseInMemory)
        {
            return UpdatePasswordInMemory(playerId, oldPasswordHash, newPasswordHash);
        }

        try
        {
            if (!Guid.TryParse(playerId, out var playerGuid))
            {
                return false;
            }

            var playerRecord = await _databaseService!.GetPlayerByIdAsync(playerGuid);
            if (playerRecord == null || playerRecord.PasswordHash == null)
            {
                return false;
            }

            // Verify old password
            if (!SecurityManager.VerifyPassword(oldPasswordHash, playerRecord.PasswordHash))
            {
                return false;
            }

            // Set new password (hash it server-side)
            var newServerPasswordHash = SecurityManager.HashPassword(newPasswordHash);
            var success = await _databaseService.UpdatePlayerPasswordAsync(playerGuid, newServerPasswordHash);

            if (success)
            {
                _logger.LogInformation("Password updated for player: {PlayerId}", playerId);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error updating password for player: {PlayerId}", playerId);
            return false;
        }
    }

    private bool UpdatePasswordInMemory(string playerId, string oldPasswordHash, string newPasswordHash)
    {
        if (!_players.TryGetValue(playerId, out var player))
        {
            return false;
        }

        // Verify old password
        if (!SecurityManager.VerifyPassword(oldPasswordHash, player.PasswordHash))
        {
            return false;
        }

        // Set new password
        player.PasswordHash = SecurityManager.HashPassword(newPasswordHash);

        _logger.LogInformation("Password updated for player: {Username}", player.Username);

        return true;
    }

    #endregion

    #region Guest Players

    /// <summary>
    /// Creates a test/guest player (for development/testing)
    /// </summary>
    /// <param name="username">Guest username</param>
    /// <returns>The guest player data</returns>
    public PlayerData CreateGuestPlayer(string? username = null)
    {
        var playerId = SecurityManager.GeneratePlayerId();
        username ??= $"Guest_{playerId[^6..]}";

        var player = new PlayerData
        {
            PlayerId = playerId,
            Username = username,
            Email = $"{playerId}@guest.local",
            PasswordHash = SecurityManager.HashPassword(Guid.NewGuid().ToString()),
            Rating = _defaultRating,
            CreatedAt = DateTime.UtcNow,
            IsGuest = true
        };

        // Guest players are always stored in memory (not persisted to DB)
        _players.TryAdd(playerId, player);
        _playersByUsername.TryAdd(username.ToLowerInvariant(), player);

        _logger.LogDebug("Guest player created: {Username}", username);

        return player;
    }

    #endregion

    #region Mapping Helpers

    /// <summary>
    /// Maps a database PlayerRecord to PlayerData
    /// </summary>
    private static PlayerData MapRecordToPlayerData(PlayerRecord record)
    {
        return new PlayerData
        {
            PlayerId = record.PlayerId.ToString(),
            Username = record.Username,
            Email = record.Email,
            PasswordHash = record.PasswordHash ?? string.Empty,
            Rating = record.Rating,
            CreatedAt = record.CreatedAt,
            LastLoginAt = record.LastLoginAt,
            GamesPlayed = record.GamesPlayed,
            Wins = record.GamesWon,
            Losses = record.GamesLost,
            Draws = record.GamesDrawn,
            IsGuest = false,
            IsBanned = record.IsBanned,
            BanReason = record.BanReason
        };
    }

    #endregion
}

/// <summary>
/// Represents a player's stored data
/// </summary>
public sealed class PlayerData
{
    public required string PlayerId { get; init; }
    public required string Username { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public int Rating { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastLoginAt { get; set; }
    public int GamesPlayed { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Draws { get; set; }
    public bool IsGuest { get; init; }
    public bool IsBanned { get; set; }
    public string? BanReason { get; set; }
}

/// <summary>
/// Result of a registration attempt
/// </summary>
public sealed class RegistrationResult
{
    public bool Success { get; init; }
    public string? PlayerId { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Player statistics for display
/// </summary>
public sealed class PlayerStatistics
{
    public required string PlayerId { get; init; }
    public required string Username { get; init; }
    public int Rating { get; init; }
    public int GamesPlayed { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public int Draws { get; init; }
    public double WinRate { get; init; }
    public int Rank { get; init; }
    public DateTime MemberSince { get; init; }
    public DateTime? LastPlayed { get; init; }
}
