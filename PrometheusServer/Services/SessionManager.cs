using System.Collections.Concurrent;
using ChessCore.Security;
using Microsoft.Extensions.Logging;
using PrometheusServer.Data;

namespace PrometheusServer.Services;

/// <summary>
/// Manages player sessions with support for DB-backed storage or in-memory fallback.
/// Wraps JWT token generation/validation with server-side session tracking.
/// </summary>
public sealed class SessionManager
{
    private readonly ILogger<SessionManager> _logger;
    private readonly DatabaseService _databaseService;
    private readonly DatabaseConfiguration _dbConfig;
    private readonly SecurityManager _securityManager;
    private readonly TimeSpan _sessionExpiration;
    private readonly int _maxSessionsPerPlayer;

    // In-memory session storage (used when UseInMemory = true)
    private readonly ConcurrentDictionary<string, SessionData> _sessions = new();
    private readonly ConcurrentDictionary<Guid, HashSet<string>> _playerSessions = new();
    private readonly object _sessionLock = new();

    // Session cleanup timer
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Gets whether the manager is using in-memory storage.
    /// </summary>
    public bool IsInMemoryMode => _dbConfig.UseInMemory;

    /// <summary>
    /// Creates a new SessionManager
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="databaseService">Database service for persistent storage</param>
    /// <param name="dbConfig">Database configuration</param>
    /// <param name="securityManager">Security manager for JWT operations</param>
    /// <param name="sessionExpirationHours">Session expiration in hours</param>
    /// <param name="maxSessionsPerPlayer">Maximum concurrent sessions per player</param>
    public SessionManager(
        ILogger<SessionManager> logger,
        DatabaseService databaseService,
        DatabaseConfiguration dbConfig,
        SecurityManager securityManager,
        int sessionExpirationHours = 24,
        int maxSessionsPerPlayer = 5)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
        _dbConfig = dbConfig ?? throw new ArgumentNullException(nameof(dbConfig));
        _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
        _sessionExpiration = TimeSpan.FromHours(sessionExpirationHours);
        _maxSessionsPerPlayer = maxSessionsPerPlayer;

        // Start cleanup timer
        _cleanupTimer = new Timer(
            _ => _ = CleanupExpiredSessionsAsync(),
            null,
            _cleanupInterval,
            _cleanupInterval);

        var storageMode = _dbConfig.UseInMemory ? "in-memory" : "PostgreSQL";
        _logger.LogDebug("SessionManager initialized with {StorageMode} storage, expiration {Hours}h, max {MaxSessions} sessions/player",
            storageMode, sessionExpirationHours, maxSessionsPerPlayer);
    }

    #region Session Creation

    /// <summary>
    /// Creates a new session for a player after successful authentication.
    /// </summary>
    /// <param name="playerId">The player's ID (string format)</param>
    /// <param name="username">The player's username</param>
    /// <param name="ipAddress">Client IP address (optional)</param>
    /// <param name="userAgent">Client user agent (optional)</param>
    /// <returns>Session creation result with token</returns>
    public async Task<SessionCreationResult> CreateSessionAsync(
        string playerId,
        string username,
        string? ipAddress = null,
        string? userAgent = null)
    {
        if (!Guid.TryParse(playerId, out var playerGuid))
        {
            _logger.LogWarning("Invalid player ID format for session creation: {PlayerId}", playerId);
            return new SessionCreationResult
            {
                Success = false,
                ErrorMessage = "Invalid player ID format"
            };
        }

        // Generate JWT token
        var token = _securityManager.GenerateToken(playerId, username);
        var expiresAt = DateTime.UtcNow.Add(_sessionExpiration);

        if (_dbConfig.UseInMemory)
        {
            return await CreateSessionInMemoryAsync(playerGuid, playerId, username, token, expiresAt, ipAddress, userAgent);
        }

        return await CreateSessionDatabaseAsync(playerGuid, playerId, username, token, expiresAt, ipAddress, userAgent);
    }

    private Task<SessionCreationResult> CreateSessionInMemoryAsync(
        Guid playerGuid,
        string playerId,
        string username,
        string token,
        DateTime expiresAt,
        string? ipAddress,
        string? userAgent)
    {
        lock (_sessionLock)
        {
            // Check session limit
            if (_playerSessions.TryGetValue(playerGuid, out var existingSessions))
            {
                // Clean up expired sessions first
                var expiredTokens = existingSessions
                    .Where(t => _sessions.TryGetValue(t, out var s) && s.ExpiresAt <= DateTime.UtcNow)
                    .ToList();

                foreach (var expiredToken in expiredTokens)
                {
                    existingSessions.Remove(expiredToken);
                    _sessions.TryRemove(expiredToken, out _);
                }

                // Check if we need to revoke oldest session
                if (existingSessions.Count >= _maxSessionsPerPlayer)
                {
                    var oldestToken = existingSessions
                        .Select(t => _sessions.TryGetValue(t, out var s) ? (Token: t, Session: s) : default)
                        .Where(x => x.Session != null)
                        .OrderBy(x => x.Session!.CreatedAt)
                        .FirstOrDefault().Token;

                    if (oldestToken != null)
                    {
                        existingSessions.Remove(oldestToken);
                        _sessions.TryRemove(oldestToken, out _);
                        _logger.LogDebug("Revoked oldest session for player {PlayerId} due to session limit", playerId);
                    }
                }
            }
            else
            {
                existingSessions = new HashSet<string>();
                _playerSessions[playerGuid] = existingSessions;
            }

            // Create new session
            var sessionId = Guid.NewGuid();
            var session = new SessionData
            {
                SessionId = sessionId,
                PlayerGuid = playerGuid,
                PlayerId = playerId,
                Username = username,
                Token = token,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt,
                LastActivity = DateTime.UtcNow,
                IpAddress = ipAddress,
                UserAgent = userAgent,
                IsRevoked = false
            };

            _sessions[token] = session;
            existingSessions.Add(token);
        }

        _logger.LogDebug("Created session (in-memory) for player {Username} ({PlayerId})", username, playerId);

        return Task.FromResult(new SessionCreationResult
        {
            Success = true,
            Token = token,
            ExpiresAt = expiresAt,
            ExpiresAtUnix = new DateTimeOffset(expiresAt).ToUnixTimeMilliseconds()
        });
    }

    private async Task<SessionCreationResult> CreateSessionDatabaseAsync(
        Guid playerGuid,
        string playerId,
        string username,
        string token,
        DateTime expiresAt,
        string? ipAddress,
        string? userAgent)
    {
        try
        {
            // Check session limit
            var activeCount = await _databaseService.GetActiveSessionCountAsync(playerGuid);
            if (activeCount >= _maxSessionsPerPlayer)
            {
                // Revoke oldest sessions to make room
                _logger.LogDebug("Player {PlayerId} has {Count} active sessions, revoking oldest",
                    playerId, activeCount);

                // We'll just revoke all and create new one (simpler approach)
                // In production, you might want to revoke only the oldest
                await _databaseService.RevokeAllPlayerSessionsAsync(playerGuid, "Session limit exceeded");
            }

            // Create session in database
            var sessionRecord = await _databaseService.CreateSessionAsync(
                playerGuid,
                token,
                _sessionExpiration,
                ipAddress,
                userAgent);

            if (sessionRecord == null)
            {
                return new SessionCreationResult
                {
                    Success = false,
                    ErrorMessage = "Failed to create session in database"
                };
            }

            _logger.LogDebug("Created session (database) for player {Username} ({PlayerId}): {SessionId}",
                username, playerId, sessionRecord.SessionId);

            return new SessionCreationResult
            {
                Success = true,
                SessionId = sessionRecord.SessionId,
                Token = token,
                ExpiresAt = expiresAt,
                ExpiresAtUnix = new DateTimeOffset(expiresAt).ToUnixTimeMilliseconds()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error creating session for player: {PlayerId}", playerId);
            return new SessionCreationResult
            {
                Success = false,
                ErrorMessage = "Database error creating session"
            };
        }
    }

    #endregion

    #region Session Validation

    /// <summary>
    /// Validates a session token. Checks both JWT validity and server-side session state.
    /// </summary>
    /// <param name="token">The session token to validate</param>
    /// <param name="updateActivity">Whether to update last activity timestamp</param>
    /// <returns>Session validation result</returns>
    public async Task<SessionValidationResult> ValidateSessionAsync(string token, bool updateActivity = true)
    {
        if (string.IsNullOrEmpty(token))
        {
            return new SessionValidationResult
            {
                IsValid = false,
                ErrorMessage = "Token is empty"
            };
        }

        // First, validate JWT signature and expiration
        var jwtResult = _securityManager.ValidateToken(token);
        if (!jwtResult.IsValid)
        {
            return new SessionValidationResult
            {
                IsValid = false,
                ErrorMessage = jwtResult.ErrorMessage ?? "Invalid token"
            };
        }

        // Then, check server-side session state
        if (_dbConfig.UseInMemory)
        {
            return await ValidateSessionInMemoryAsync(token, jwtResult, updateActivity);
        }

        return await ValidateSessionDatabaseAsync(token, jwtResult, updateActivity);
    }

    private Task<SessionValidationResult> ValidateSessionInMemoryAsync(
        string token,
        TokenValidationResult jwtResult,
        bool updateActivity)
    {
        if (!_sessions.TryGetValue(token, out var session))
        {
            return Task.FromResult(new SessionValidationResult
            {
                IsValid = false,
                ErrorMessage = "Session not found"
            });
        }

        if (session.IsRevoked)
        {
            return Task.FromResult(new SessionValidationResult
            {
                IsValid = false,
                ErrorMessage = "Session has been revoked"
            });
        }

        if (session.ExpiresAt <= DateTime.UtcNow)
        {
            return Task.FromResult(new SessionValidationResult
            {
                IsValid = false,
                ErrorMessage = "Session has expired"
            });
        }

        if (updateActivity)
        {
            session.LastActivity = DateTime.UtcNow;
        }

        return Task.FromResult(new SessionValidationResult
        {
            IsValid = true,
            SessionId = session.SessionId,
            PlayerId = session.PlayerId,
            Username = session.Username,
            ExpiresAt = session.ExpiresAt
        });
    }

    private async Task<SessionValidationResult> ValidateSessionDatabaseAsync(
        string token,
        TokenValidationResult jwtResult,
        bool updateActivity)
    {
        try
        {
            var sessionRecord = await _databaseService.GetSessionByTokenAsync(token);

            if (sessionRecord == null)
            {
                return new SessionValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Session not found or expired"
                };
            }

            if (sessionRecord.IsRevoked)
            {
                return new SessionValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Session has been revoked: {sessionRecord.RevokedReason ?? "No reason"}"
                };
            }

            if (updateActivity)
            {
                await _databaseService.UpdateSessionActivityAsync(sessionRecord.SessionId);
            }

            return new SessionValidationResult
            {
                IsValid = true,
                SessionId = sessionRecord.SessionId,
                PlayerId = jwtResult.PlayerId!,
                Username = jwtResult.Username,
                ExpiresAt = sessionRecord.ExpiresAt
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error validating session");
            return new SessionValidationResult
            {
                IsValid = false,
                ErrorMessage = "Database error validating session"
            };
        }
    }

    /// <summary>
    /// Quick validation that checks JWT only (for high-frequency operations).
    /// Use ValidateSessionAsync for full validation including revocation check.
    /// </summary>
    public TokenValidationResult ValidateTokenQuick(string token)
    {
        return _securityManager.ValidateToken(token);
    }

    #endregion

    #region Session Revocation

    /// <summary>
    /// Revokes a session by token (logout).
    /// </summary>
    /// <param name="token">The session token to revoke</param>
    /// <param name="reason">Reason for revocation</param>
    /// <returns>True if session was revoked</returns>
    public async Task<bool> RevokeSessionAsync(string token, string reason = "Logged out")
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        if (_dbConfig.UseInMemory)
        {
            return RevokeSessionInMemory(token, reason);
        }

        return await RevokeSessionDatabaseAsync(token, reason);
    }

    private bool RevokeSessionInMemory(string token, string reason)
    {
        if (!_sessions.TryGetValue(token, out var session))
        {
            return false;
        }

        session.IsRevoked = true;
        session.RevokedReason = reason;

        // Remove from player sessions
        if (_playerSessions.TryGetValue(session.PlayerGuid, out var playerSessions))
        {
            lock (_sessionLock)
            {
                playerSessions.Remove(token);
            }
        }

        _logger.LogDebug("Session revoked (in-memory) for player {PlayerId}: {Reason}",
            session.PlayerId, reason);

        return true;
    }

    private async Task<bool> RevokeSessionDatabaseAsync(string token, string reason)
    {
        try
        {
            var sessionRecord = await _databaseService.GetSessionByTokenAsync(token);
            if (sessionRecord == null)
            {
                return false;
            }

            var success = await _databaseService.RevokeSessionAsync(sessionRecord.SessionId, reason);

            if (success)
            {
                _logger.LogDebug("Session revoked (database): {SessionId}, Reason: {Reason}",
                    sessionRecord.SessionId, reason);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error revoking session");
            return false;
        }
    }

    /// <summary>
    /// Revokes all sessions for a player (force logout from all devices).
    /// </summary>
    /// <param name="playerId">The player's ID</param>
    /// <param name="reason">Reason for revocation</param>
    /// <returns>Number of sessions revoked</returns>
    public async Task<int> RevokeAllPlayerSessionsAsync(string playerId, string reason = "All sessions revoked")
    {
        if (!Guid.TryParse(playerId, out var playerGuid))
        {
            return 0;
        }

        if (_dbConfig.UseInMemory)
        {
            return RevokeAllPlayerSessionsInMemory(playerGuid, reason);
        }

        return await RevokeAllPlayerSessionsDatabaseAsync(playerGuid, reason);
    }

    private int RevokeAllPlayerSessionsInMemory(Guid playerGuid, string reason)
    {
        if (!_playerSessions.TryGetValue(playerGuid, out var playerSessions))
        {
            return 0;
        }

        int count;
        lock (_sessionLock)
        {
            count = playerSessions.Count;
            foreach (var token in playerSessions.ToList())
            {
                if (_sessions.TryGetValue(token, out var session))
                {
                    session.IsRevoked = true;
                    session.RevokedReason = reason;
                }
            }
            playerSessions.Clear();
        }

        _logger.LogInformation("Revoked {Count} sessions (in-memory) for player {PlayerId}: {Reason}",
            count, playerGuid, reason);

        return count;
    }

    private async Task<int> RevokeAllPlayerSessionsDatabaseAsync(Guid playerGuid, string reason)
    {
        try
        {
            var count = await _databaseService.RevokeAllPlayerSessionsAsync(playerGuid, reason);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error revoking all sessions for player: {PlayerId}", playerGuid);
            return 0;
        }
    }

    #endregion

    #region Session Queries

    /// <summary>
    /// Gets the number of active sessions for a player.
    /// </summary>
    public async Task<int> GetActiveSessionCountAsync(string playerId)
    {
        if (!Guid.TryParse(playerId, out var playerGuid))
        {
            return 0;
        }

        if (_dbConfig.UseInMemory)
        {
            if (!_playerSessions.TryGetValue(playerGuid, out var playerSessions))
            {
                return 0;
            }

            lock (_sessionLock)
            {
                return playerSessions.Count(t =>
                    _sessions.TryGetValue(t, out var s) &&
                    !s.IsRevoked &&
                    s.ExpiresAt > DateTime.UtcNow);
            }
        }

        try
        {
            return await _databaseService.GetActiveSessionCountAsync(playerGuid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error getting active session count for player: {PlayerId}", playerId);
            return 0;
        }
    }

    /// <summary>
    /// Gets total active session count across all players (for monitoring).
    /// </summary>
    public int GetTotalActiveSessionCount()
    {
        if (_dbConfig.UseInMemory)
        {
            return _sessions.Values.Count(s => !s.IsRevoked && s.ExpiresAt > DateTime.UtcNow);
        }

        // For database mode, this would require a separate query
        // Not implementing async property, return -1 to indicate "use async method"
        return -1;
    }

    #endregion

    #region Cleanup

    /// <summary>
    /// Cleans up expired and revoked sessions.
    /// </summary>
    public async Task<int> CleanupExpiredSessionsAsync()
    {
        if (_dbConfig.UseInMemory)
        {
            return CleanupExpiredSessionsInMemory();
        }

        try
        {
            return await _databaseService.CleanupExpiredSessionsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database error during session cleanup");
            return 0;
        }
    }

    private int CleanupExpiredSessionsInMemory()
    {
        var now = DateTime.UtcNow;
        var expiredTokens = _sessions
            .Where(kvp => kvp.Value.IsRevoked || kvp.Value.ExpiresAt <= now)
            .Select(kvp => kvp.Key)
            .ToList();

        int count = 0;
        lock (_sessionLock)
        {
            foreach (var token in expiredTokens)
            {
                if (_sessions.TryRemove(token, out var session))
                {
                    count++;
                    if (_playerSessions.TryGetValue(session.PlayerGuid, out var playerSessions))
                    {
                        playerSessions.Remove(token);
                    }
                }
            }
        }

        if (count > 0)
        {
            _logger.LogDebug("Cleaned up {Count} expired/revoked sessions (in-memory)", count);
        }

        return count;
    }

    #endregion
}

#region Data Models

/// <summary>
/// In-memory session data
/// </summary>
public sealed class SessionData
{
    public required Guid SessionId { get; init; }
    public required Guid PlayerGuid { get; init; }
    public required string PlayerId { get; init; }
    public required string Username { get; init; }
    public required string Token { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public DateTime LastActivity { get; set; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public bool IsRevoked { get; set; }
    public string? RevokedReason { get; set; }
}

/// <summary>
/// Result of session creation
/// </summary>
public sealed class SessionCreationResult
{
    public bool Success { get; init; }
    public Guid? SessionId { get; init; }
    public string? Token { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public long? ExpiresAtUnix { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of session validation
/// </summary>
public sealed class SessionValidationResult
{
    public bool IsValid { get; init; }
    public Guid? SessionId { get; init; }
    public string? PlayerId { get; init; }
    public string? Username { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string? ErrorMessage { get; init; }
}

#endregion
