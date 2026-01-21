using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace PrometheusServer.Data;

/// <summary>
/// Service for all PostgreSQL database operations.
/// Handles players, sessions, games, and moves.
/// Should you need to setup database, you need to look into DatabaseConfiguration.cs first.
/// </summary>
public sealed class DatabaseService : IAsyncDisposable
{
    private readonly ILogger<DatabaseService> _logger;
    private readonly DatabaseConfiguration _config;
    private readonly NpgsqlDataSource _dataSource;
    private bool _isDisposed;

    /// <summary>
    /// Creates a new DatabaseService instance.
    /// </summary>
    public DatabaseService(ILogger<DatabaseService> logger, DatabaseConfiguration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        var connectionString = config.BuildConnectionString();
        _dataSource = NpgsqlDataSource.Create(connectionString);

        _logger.LogInformation("DatabaseService initialized for {Host}:{Port}/{Database}",
            config.Host, config.Port, config.Database);
    }

    #region Connection Management

    /// <summary>
    /// Tests the database connection.
    /// </summary>
    /// <returns>True if connection is successful</returns>
    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);

            _logger.LogInformation("Database connection test successful");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database connection test failed");
            return false;
        }
    }

    /// <summary>
    /// Gets database server version information.
    /// </summary>
    public async Task<string?> GetServerVersionAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT version()";
            var result = await cmd.ExecuteScalarAsync(ct);
            return result?.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get server version");
            return null;
        }
    }

    #endregion

    #region Player Operations

    /// <summary>
    /// Creates a new player in the database.
    /// </summary>
    public async Task<PlayerRecord?> CreatePlayerAsync(
        string username,
        string email,
        string passwordHash,
        int defaultRating = 1200,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                INSERT INTO players (username, email, password_hash, rating)
                VALUES (@username, @email, @password_hash, @rating)
                RETURNING player_id, username, email, rating, games_played, games_won,
                          games_lost, games_drawn, created_at, last_login_at, is_banned, ban_reason";

            cmd.Parameters.AddWithValue("username", username);
            cmd.Parameters.AddWithValue("email", email.ToLowerInvariant());
            cmd.Parameters.AddWithValue("password_hash", passwordHash);
            cmd.Parameters.AddWithValue("rating", defaultRating);

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                var player = MapPlayerRecord(reader);
                _logger.LogInformation("Created new player: {Username} ({PlayerId})",
                    player.Username, player.PlayerId);
                return player;
            }

            return null;
        }
        catch (PostgresException ex) when (ex.SqlState == "23505") // Unique violation
        {
            if (ex.ConstraintName?.Contains("username") == true)
            {
                _logger.LogWarning("Registration failed: Username '{Username}' already exists", username);
            }
            else if (ex.ConstraintName?.Contains("email") == true)
            {
                _logger.LogWarning("Registration failed: Email '{Email}' already exists", email);
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create player: {Username}", username);
            throw;
        }
    }

    /// <summary>
    /// Gets a player by their ID.
    /// </summary>
    public async Task<PlayerRecord?> GetPlayerByIdAsync(Guid playerId, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT player_id, username, email, password_hash, rating, games_played,
                       games_won, games_lost, games_drawn, created_at, last_login_at, is_banned, ban_reason
                FROM players
                WHERE player_id = @player_id";

            cmd.Parameters.AddWithValue("player_id", playerId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                return MapPlayerRecord(reader, includePasswordHash: true);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get player by ID: {PlayerId}", playerId);
            throw;
        }
    }

    /// <summary>
    /// Gets a player by their username.
    /// </summary>
    public async Task<PlayerRecord?> GetPlayerByUsernameAsync(string username, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT player_id, username, email, password_hash, rating, games_played,
                       games_won, games_lost, games_drawn, created_at, last_login_at, is_banned, ban_reason
                FROM players
                WHERE LOWER(username) = LOWER(@username)";

            cmd.Parameters.AddWithValue("username", username.Trim());

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                return MapPlayerRecord(reader, includePasswordHash: true);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get player by username: {Username}", username);
            throw;
        }
    }

    /// <summary>
    /// Gets a player by their email.
    /// </summary>
    public async Task<PlayerRecord?> GetPlayerByEmailAsync(string email, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT player_id, username, email, password_hash, rating, games_played,
                       games_won, games_lost, games_drawn, created_at, last_login_at, is_banned, ban_reason
                FROM players
                WHERE LOWER(email) = LOWER(@email)";

            cmd.Parameters.AddWithValue("email", email.Trim());

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                return MapPlayerRecord(reader, includePasswordHash: true);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get player by email: {Email}", email);
            throw;
        }
    }

    /// <summary>
    /// Updates a player's last login timestamp.
    /// </summary>
    public async Task UpdateLastLoginAsync(Guid playerId, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE players
                SET last_login_at = NOW()
                WHERE player_id = @player_id";

            cmd.Parameters.AddWithValue("player_id", playerId);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update last login for player: {PlayerId}", playerId);
        }
    }

    /// <summary>
    /// Updates a player's game statistics (games played, wins, losses, draws).
    /// </summary>
    /// <param name="playerId">The player's ID</param>
    /// <param name="result">Game result: 1.0 = win, 0.5 = draw, 0.0 = loss</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<bool> UpdateGameStatsAsync(
        Guid playerId,
        double result,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            // Determine which stat column to increment based on result
            string statColumn;
            if (result > 0.9)
                statColumn = "games_won";
            else if (result < 0.1)
                statColumn = "games_lost";
            else
                statColumn = "games_drawn";

            cmd.CommandText = $@"
                UPDATE players
                SET games_played = games_played + 1,
                    {statColumn} = {statColumn} + 1
                WHERE player_id = @player_id";

            cmd.Parameters.AddWithValue("player_id", playerId);

            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

            if (rowsAffected > 0)
            {
                _logger.LogDebug("Updated game stats for player {PlayerId}: {StatColumn} incremented",
                    playerId, statColumn);
            }

            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update game stats for player: {PlayerId}", playerId);
            throw;
        }
    }

    /// <summary>
    /// Updates a player's rating.
    /// </summary>
    public async Task<bool> UpdatePlayerRatingAsync(
        Guid playerId,
        int newRating,
        int ratingChange,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE players
                SET rating = @rating
                WHERE player_id = @player_id";

            cmd.Parameters.AddWithValue("player_id", playerId);
            cmd.Parameters.AddWithValue("rating", newRating);

            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

            if (rowsAffected > 0)
            {
                _logger.LogDebug("Updated rating for player {PlayerId}: {RatingChange:+#;-#;0} -> {NewRating}",
                    playerId, ratingChange, newRating);
            }

            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update rating for player: {PlayerId}", playerId);
            throw;
        }
    }

    /// <summary>
    /// Updates a player's password.
    /// </summary>
    public async Task<bool> UpdatePlayerPasswordAsync(
        Guid playerId,
        string newPasswordHash,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE players
                SET password_hash = @password_hash
                WHERE player_id = @player_id";

            cmd.Parameters.AddWithValue("player_id", playerId);
            cmd.Parameters.AddWithValue("password_hash", newPasswordHash);

            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

            if (rowsAffected > 0)
            {
                _logger.LogInformation("Password updated for player: {PlayerId}", playerId);
            }

            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update password for player: {PlayerId}", playerId);
            throw;
        }
    }

    /// <summary>
    /// Bans a player.
    /// </summary>
    public async Task<bool> BanPlayerAsync(
        Guid playerId,
        string reason,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE players
                SET is_banned = TRUE, ban_reason = @reason
                WHERE player_id = @player_id";

            cmd.Parameters.AddWithValue("player_id", playerId);
            cmd.Parameters.AddWithValue("reason", reason);

            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

            if (rowsAffected > 0)
            {
                _logger.LogWarning("Player banned: {PlayerId}, Reason: {Reason}", playerId, reason);
            }

            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ban player: {PlayerId}", playerId);
            throw;
        }
    }

    /// <summary>
    /// Unbans a player.
    /// </summary>
    public async Task<bool> UnbanPlayerAsync(Guid playerId, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE players
                SET is_banned = FALSE, ban_reason = NULL
                WHERE player_id = @player_id";

            cmd.Parameters.AddWithValue("player_id", playerId);

            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

            if (rowsAffected > 0)
            {
                _logger.LogInformation("Player unbanned: {PlayerId}", playerId);
            }

            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unban player: {PlayerId}", playerId);
            throw;
        }
    }

    /// <summary>
    /// Checks if a username is available.
    /// </summary>
    public async Task<bool> IsUsernameAvailableAsync(string username, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT EXISTS(
                    SELECT 1 FROM players WHERE LOWER(username) = LOWER(@username)
                )";

            cmd.Parameters.AddWithValue("username", username.Trim());

            var exists = (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
            return !exists;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check username availability: {Username}", username);
            throw;
        }
    }

    /// <summary>
    /// Checks if an email is available.
    /// </summary>
    public async Task<bool> IsEmailAvailableAsync(string email, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT EXISTS(
                    SELECT 1 FROM players WHERE LOWER(email) = LOWER(@email)
                )";

            cmd.Parameters.AddWithValue("email", email.Trim());

            var exists = (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
            return !exists;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check email availability: {Email}", email);
            throw;
        }
    }

    /// <summary>
    /// Gets the leaderboard (top players by rating).
    /// </summary>
    public async Task<List<PlayerRecord>> GetLeaderboardAsync(int limit = 100, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT player_id, username, email, rating, games_played,
                       games_won, games_lost, games_drawn, created_at, last_login_at, is_banned, ban_reason
                FROM players
                WHERE is_banned = FALSE
                ORDER BY rating DESC, games_won DESC
                LIMIT @limit";

            cmd.Parameters.AddWithValue("limit", limit);

            var players = new List<PlayerRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                players.Add(MapPlayerRecord(reader));
            }

            return players;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get leaderboard");
            throw;
        }
    }

    /// <summary>
    /// Gets a player's rank on the leaderboard.
    /// </summary>
    public async Task<int> GetPlayerRankAsync(Guid playerId, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT COUNT(*) + 1
                FROM players p1
                WHERE p1.rating > (SELECT rating FROM players WHERE player_id = @player_id)
                  AND p1.is_banned = FALSE";

            cmd.Parameters.AddWithValue("player_id", playerId);

            var rank = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0);
            return (int)rank;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get player rank: {PlayerId}", playerId);
            throw;
        }
    }

    /// <summary>
    /// Gets total player count.
    /// </summary>
    public async Task<int> GetTotalPlayerCountAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = "SELECT COUNT(*) FROM players";

            var count = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0);
            return (int)count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get total player count");
            throw;
        }
    }

    private static PlayerRecord MapPlayerRecord(NpgsqlDataReader reader, bool includePasswordHash = false)
    {
        // Helper to safely check if a column exists
        static bool HasColumn(NpgsqlDataReader r, string columnName)
        {
            try
            {
                return r.GetOrdinal(columnName) >= 0;
            }
            catch (IndexOutOfRangeException)
            {
                return false;
            }
        }

        // Read password hash if requested and column exists
        string? passwordHash = null;
        if (includePasswordHash && HasColumn(reader, "password_hash"))
        {
            var pwOrdinal = reader.GetOrdinal("password_hash");
            if (!reader.IsDBNull(pwOrdinal))
            {
                passwordHash = reader.GetString(pwOrdinal);
            }
        }

        // Read ban_reason if column exists
        string? banReason = null;
        if (HasColumn(reader, "ban_reason"))
        {
            var banOrdinal = reader.GetOrdinal("ban_reason");
            if (!reader.IsDBNull(banOrdinal))
            {
                banReason = reader.GetString(banOrdinal);
            }
        }

        return new PlayerRecord
        {
            PlayerId = reader.GetGuid(reader.GetOrdinal("player_id")),
            Username = reader.GetString(reader.GetOrdinal("username")),
            Email = reader.GetString(reader.GetOrdinal("email")),
            PasswordHash = passwordHash,
            Rating = reader.GetInt32(reader.GetOrdinal("rating")),
            GamesPlayed = reader.GetInt32(reader.GetOrdinal("games_played")),
            GamesWon = reader.GetInt32(reader.GetOrdinal("games_won")),
            GamesLost = reader.GetInt32(reader.GetOrdinal("games_lost")),
            GamesDrawn = reader.GetInt32(reader.GetOrdinal("games_drawn")),
            CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at")),
            LastLoginAt = reader.IsDBNull(reader.GetOrdinal("last_login_at"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("last_login_at")),
            IsBanned = reader.GetBoolean(reader.GetOrdinal("is_banned")),
            BanReason = banReason
        };
    }

    #endregion

    #region Session Operations

    /// <summary>
    /// Creates a new session for a player.
    /// </summary>
    public async Task<SessionRecord?> CreateSessionAsync(
        Guid playerId,
        string token,
        TimeSpan expiresIn,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            // Hash the token before storing
            var tokenHash = HashToken(token);

            cmd.CommandText = @"
                INSERT INTO sessions (player_id, token_hash, expires_at, ip_address, user_agent)
                VALUES (@player_id, @token_hash, @expires_at, @ip_address, @user_agent)
                RETURNING session_id, player_id, token_hash, created_at, expires_at,
                          last_activity, ip_address, user_agent, is_revoked, revoked_reason";

            cmd.Parameters.AddWithValue("player_id", playerId);
            cmd.Parameters.AddWithValue("token_hash", tokenHash);
            cmd.Parameters.AddWithValue("expires_at", DateTime.UtcNow.Add(expiresIn));
            cmd.Parameters.AddWithValue("ip_address",
                ipAddress != null ? ParseIpFromEndpoint(ipAddress) : DBNull.Value);
            cmd.Parameters.AddWithValue("user_agent", (object?)userAgent ?? DBNull.Value);

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                var session = MapSessionRecord(reader);
                _logger.LogDebug("Created session for player {PlayerId}: {SessionId}",
                    playerId, session.SessionId);
                return session;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create session for player: {PlayerId}", playerId);
            throw;
        }
    }

    /// <summary>
    /// Parses an IP address from an endpoint string (which may include a port).
    /// Handles formats like "192.168.1.1:12345", "[::1]:12345", "192.168.1.1", "::1"
    /// </summary>
    private static object ParseIpFromEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint == "Unknown")
        {
            return DBNull.Value;
        }

        // Try to parse as-is first (in case it's already a valid IP)
        if (System.Net.IPAddress.TryParse(endpoint, out var directIp))
        {
            return directIp;
        }

        // Handle IPv6 with port: [::1]:12345
        if (endpoint.StartsWith('['))
        {
            var closeBracket = endpoint.IndexOf(']');
            if (closeBracket > 1)
            {
                var ipPart = endpoint.Substring(1, closeBracket - 1);
                if (System.Net.IPAddress.TryParse(ipPart, out var ipv6))
                {
                    return ipv6;
                }
            }
        }

        // Handle IPv4 with port: 192.168.1.1:12345
        var lastColon = endpoint.LastIndexOf(':');
        if (lastColon > 0)
        {
            var ipPart = endpoint.Substring(0, lastColon);
            if (System.Net.IPAddress.TryParse(ipPart, out var ipv4))
            {
                return ipv4;
            }
        }

        // Could not parse, return null
        return DBNull.Value;
    }

    /// <summary>
    /// Gets a session by token hash.
    /// </summary>
    public async Task<SessionRecord?> GetSessionByTokenAsync(string token, CancellationToken ct = default)
    {
        try
        {
            var tokenHash = HashToken(token);

            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT session_id, player_id, token_hash, created_at, expires_at,
                       last_activity, ip_address, user_agent, is_revoked, revoked_reason
                FROM sessions
                WHERE token_hash = @token_hash
                  AND is_revoked = FALSE
                  AND expires_at > NOW()";

            cmd.Parameters.AddWithValue("token_hash", tokenHash);

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                return MapSessionRecord(reader);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get session by token");
            throw;
        }
    }

    /// <summary>
    /// Updates session last activity timestamp.
    /// </summary>
    public async Task UpdateSessionActivityAsync(Guid sessionId, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE sessions
                SET last_activity = NOW()
                WHERE session_id = @session_id";

            cmd.Parameters.AddWithValue("session_id", sessionId);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update session activity: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Revokes a session.
    /// </summary>
    public async Task<bool> RevokeSessionAsync(
        Guid sessionId,
        string reason = "Logged out",
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE sessions
                SET is_revoked = TRUE, revoked_reason = @reason
                WHERE session_id = @session_id";

            cmd.Parameters.AddWithValue("session_id", sessionId);
            cmd.Parameters.AddWithValue("reason", reason);

            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

            if (rowsAffected > 0)
            {
                _logger.LogDebug("Session revoked: {SessionId}, Reason: {Reason}", sessionId, reason);
            }

            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to revoke session: {SessionId}", sessionId);
            throw;
        }
    }

    /// <summary>
    /// Revokes all sessions for a player.
    /// </summary>
    public async Task<int> RevokeAllPlayerSessionsAsync(
        Guid playerId,
        string reason = "All sessions revoked",
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE sessions
                SET is_revoked = TRUE, revoked_reason = @reason
                WHERE player_id = @player_id AND is_revoked = FALSE";

            cmd.Parameters.AddWithValue("player_id", playerId);
            cmd.Parameters.AddWithValue("reason", reason);

            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

            _logger.LogInformation("Revoked {Count} sessions for player: {PlayerId}", rowsAffected, playerId);

            return rowsAffected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to revoke all sessions for player: {PlayerId}", playerId);
            throw;
        }
    }

    /// <summary>
    /// Gets active session count for a player.
    /// </summary>
    public async Task<int> GetActiveSessionCountAsync(Guid playerId, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT COUNT(*) FROM sessions
                WHERE player_id = @player_id
                  AND is_revoked = FALSE
                  AND expires_at > NOW()";

            cmd.Parameters.AddWithValue("player_id", playerId);

            var count = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0);
            return (int)count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active session count for player: {PlayerId}", playerId);
            throw;
        }
    }

    /// <summary>
    /// Cleans up expired sessions.
    /// </summary>
    public async Task<int> CleanupExpiredSessionsAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                DELETE FROM sessions
                WHERE expires_at < NOW() OR is_revoked = TRUE";

            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

            if (rowsAffected > 0)
            {
                _logger.LogDebug("Cleaned up {Count} expired/revoked sessions", rowsAffected);
            }

            return rowsAffected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup expired sessions");
            throw;
        }
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? TryGetString(NpgsqlDataReader reader, string columnName)
    {
        try
        {
            var ordinal = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static SessionRecord MapSessionRecord(NpgsqlDataReader reader)
    {
        var ipOrdinal = reader.GetOrdinal("ip_address");

        return new SessionRecord
        {
            SessionId = reader.GetGuid(reader.GetOrdinal("session_id")),
            PlayerId = reader.GetGuid(reader.GetOrdinal("player_id")),
            TokenHash = reader.GetString(reader.GetOrdinal("token_hash")),
            CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at")),
            ExpiresAt = reader.GetDateTime(reader.GetOrdinal("expires_at")),
            LastActivity = reader.GetDateTime(reader.GetOrdinal("last_activity")),
            IpAddress = reader.IsDBNull(ipOrdinal) ? null : reader.GetFieldValue<System.Net.IPAddress>(ipOrdinal).ToString(),
            UserAgent = reader.IsDBNull(reader.GetOrdinal("user_agent"))
                ? null
                : reader.GetString(reader.GetOrdinal("user_agent")),
            IsRevoked = reader.GetBoolean(reader.GetOrdinal("is_revoked")),
            RevokedReason = TryGetString(reader, "revoked_reason")
        };
    }

    #endregion

    #region Game Operations

    /// <summary>
    /// Creates a new game record.
    /// </summary>
    public async Task<GameRecord?> CreateGameAsync(
        Guid whitePlayerId,
        Guid blackPlayerId,
        string timeControl,
        int initialTimeMs,
        int incrementMs,
        int whiteRatingBefore,
        int blackRatingBefore,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                INSERT INTO games (white_player_id, black_player_id, status, time_control,
                                   initial_time_ms, increment_ms, white_rating_before, black_rating_before, started_at)
                VALUES (@white_player_id, @black_player_id, 'active', @time_control::time_control_type,
                        @initial_time_ms, @increment_ms, @white_rating_before, @black_rating_before, NOW())
                RETURNING game_id, white_player_id, black_player_id, status, result, end_reason,
                          time_control, initial_time_ms, increment_ms, pgn, final_fen,
                          started_at, ended_at, white_rating_before, black_rating_before,
                          white_rating_change, black_rating_change, created_at";

            cmd.Parameters.AddWithValue("white_player_id", whitePlayerId);
            cmd.Parameters.AddWithValue("black_player_id", blackPlayerId);
            cmd.Parameters.AddWithValue("time_control", timeControl.ToLowerInvariant());
            cmd.Parameters.AddWithValue("initial_time_ms", initialTimeMs);
            cmd.Parameters.AddWithValue("increment_ms", incrementMs);
            cmd.Parameters.AddWithValue("white_rating_before", whiteRatingBefore);
            cmd.Parameters.AddWithValue("black_rating_before", blackRatingBefore);

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                var game = MapGameRecord(reader);
                _logger.LogInformation("Created game {GameId}: {WhiteId} vs {BlackId}",
                    game.GameId, whitePlayerId, blackPlayerId);
                return game;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create game");
            throw;
        }
    }

    /// <summary>
    /// Gets a game by ID.
    /// </summary>
    public async Task<GameRecord?> GetGameByIdAsync(Guid gameId, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT game_id, white_player_id, black_player_id, status, result, end_reason,
                       time_control, initial_time_ms, increment_ms, pgn, final_fen,
                       started_at, ended_at, white_rating_before, black_rating_before,
                       white_rating_change, black_rating_change, created_at
                FROM games
                WHERE game_id = @game_id";

            cmd.Parameters.AddWithValue("game_id", gameId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                return MapGameRecord(reader);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get game: {GameId}", gameId);
            throw;
        }
    }

    /// <summary>
    /// Completes a game with the result.
    /// </summary>
    public async Task<bool> CompleteGameAsync(
        Guid gameId,
        string result,
        string endReason,
        string? pgn,
        string? finalFen,
        int whiteRatingChange,
        int blackRatingChange,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE games
                SET status = 'completed',
                    result = @result::game_result,
                    end_reason = @end_reason::game_end_reason,
                    pgn = @pgn,
                    final_fen = @final_fen,
                    white_rating_change = @white_rating_change,
                    black_rating_change = @black_rating_change,
                    ended_at = NOW()
                WHERE game_id = @game_id";

            cmd.Parameters.AddWithValue("game_id", gameId);
            cmd.Parameters.AddWithValue("result", result.ToLowerInvariant());
            cmd.Parameters.AddWithValue("end_reason", endReason.ToLowerInvariant());
            cmd.Parameters.AddWithValue("pgn", (object?)pgn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("final_fen", (object?)finalFen ?? DBNull.Value);
            cmd.Parameters.AddWithValue("white_rating_change", whiteRatingChange);
            cmd.Parameters.AddWithValue("black_rating_change", blackRatingChange);

            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

            if (rowsAffected > 0)
            {
                _logger.LogInformation("Game {GameId} completed: {Result} ({EndReason})",
                    gameId, result, endReason);
            }

            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete game: {GameId}", gameId);
            throw;
        }
    }

    /// <summary>
    /// Aborts a game.
    /// </summary>
    public async Task<bool> AbortGameAsync(Guid gameId, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                UPDATE games
                SET status = 'aborted',
                    result = 'aborted',
                    end_reason = 'aborted',
                    ended_at = NOW()
                WHERE game_id = @game_id AND status IN ('pending', 'active')";

            cmd.Parameters.AddWithValue("game_id", gameId);

            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);

            if (rowsAffected > 0)
            {
                _logger.LogInformation("Game {GameId} aborted", gameId);
            }

            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to abort game: {GameId}", gameId);
            throw;
        }
    }

    /// <summary>
    /// Gets a player's game history.
    /// </summary>
    public async Task<List<GameRecord>> GetPlayerGamesAsync(
        Guid playerId,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT game_id, white_player_id, black_player_id, status, result, end_reason,
                       time_control, initial_time_ms, increment_ms, pgn, final_fen,
                       started_at, ended_at, white_rating_before, black_rating_before,
                       white_rating_change, black_rating_change, created_at
                FROM games
                WHERE white_player_id = @player_id OR black_player_id = @player_id
                ORDER BY created_at DESC
                LIMIT @limit OFFSET @offset";

            cmd.Parameters.AddWithValue("player_id", playerId);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);

            var games = new List<GameRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                games.Add(MapGameRecord(reader));
            }

            return games;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get games for player: {PlayerId}", playerId);
            throw;
        }
    }

    private static GameRecord MapGameRecord(NpgsqlDataReader reader)
    {
        return new GameRecord
        {
            GameId = reader.GetGuid(reader.GetOrdinal("game_id")),
            WhitePlayerId = reader.GetGuid(reader.GetOrdinal("white_player_id")),
            BlackPlayerId = reader.GetGuid(reader.GetOrdinal("black_player_id")),
            Status = reader.GetString(reader.GetOrdinal("status")),
            Result = reader.IsDBNull(reader.GetOrdinal("result"))
                ? null
                : reader.GetString(reader.GetOrdinal("result")),
            EndReason = reader.IsDBNull(reader.GetOrdinal("end_reason"))
                ? null
                : reader.GetString(reader.GetOrdinal("end_reason")),
            TimeControl = reader.GetString(reader.GetOrdinal("time_control")),
            InitialTimeMs = reader.GetInt32(reader.GetOrdinal("initial_time_ms")),
            IncrementMs = reader.GetInt32(reader.GetOrdinal("increment_ms")),
            Pgn = reader.IsDBNull(reader.GetOrdinal("pgn"))
                ? null
                : reader.GetString(reader.GetOrdinal("pgn")),
            FinalFen = reader.IsDBNull(reader.GetOrdinal("final_fen"))
                ? null
                : reader.GetString(reader.GetOrdinal("final_fen")),
            StartedAt = reader.IsDBNull(reader.GetOrdinal("started_at"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("started_at")),
            EndedAt = reader.IsDBNull(reader.GetOrdinal("ended_at"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("ended_at")),
            WhiteRatingBefore = reader.IsDBNull(reader.GetOrdinal("white_rating_before"))
                ? null
                : reader.GetInt32(reader.GetOrdinal("white_rating_before")),
            BlackRatingBefore = reader.IsDBNull(reader.GetOrdinal("black_rating_before"))
                ? null
                : reader.GetInt32(reader.GetOrdinal("black_rating_before")),
            WhiteRatingChange = reader.IsDBNull(reader.GetOrdinal("white_rating_change"))
                ? null
                : reader.GetInt32(reader.GetOrdinal("white_rating_change")),
            BlackRatingChange = reader.IsDBNull(reader.GetOrdinal("black_rating_change"))
                ? null
                : reader.GetInt32(reader.GetOrdinal("black_rating_change")),
            CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at"))
        };
    }

    #endregion

    #region Game Moves Operations

    /// <summary>
    /// Records a move in a game.
    /// </summary>
    public async Task<bool> RecordMoveAsync(
        Guid gameId,
        int moveNumber,
        string color,
        string fromSquare,
        string toSquare,
        string? promotion,
        string? sanNotation,
        string fenAfter,
        int? timeRemainingMs,
        int? moveTimeMs,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                INSERT INTO game_moves (game_id, move_number, color, from_square, to_square,
                                        promotion, san_notation, fen_after, time_remaining_ms, move_time_ms)
                VALUES (@game_id, @move_number, @color::piece_color, @from_square, @to_square,
                        @promotion, @san_notation, @fen_after, @time_remaining_ms, @move_time_ms)";

            cmd.Parameters.AddWithValue("game_id", gameId);
            cmd.Parameters.AddWithValue("move_number", moveNumber);
            cmd.Parameters.AddWithValue("color", color.ToLowerInvariant());
            cmd.Parameters.AddWithValue("from_square", fromSquare.ToLowerInvariant());
            cmd.Parameters.AddWithValue("to_square", toSquare.ToLowerInvariant());
            cmd.Parameters.AddWithValue("promotion", (object?)promotion?.ToLowerInvariant() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("san_notation", (object?)sanNotation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("fen_after", fenAfter);
            cmd.Parameters.AddWithValue("time_remaining_ms", (object?)timeRemainingMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("move_time_ms", (object?)moveTimeMs ?? DBNull.Value);

            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record move for game: {GameId}", gameId);
            throw;
        }
    }

    /// <summary>
    /// Gets all moves for a game.
    /// </summary>
    public async Task<List<GameMoveRecord>> GetGameMovesAsync(Guid gameId, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            cmd.CommandText = @"
                SELECT move_id, game_id, move_number, color, from_square, to_square,
                       promotion, san_notation, fen_after, time_remaining_ms, move_time_ms, created_at
                FROM game_moves
                WHERE game_id = @game_id
                ORDER BY move_number, color";

            cmd.Parameters.AddWithValue("game_id", gameId);

            var moves = new List<GameMoveRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                moves.Add(new GameMoveRecord
                {
                    MoveId = reader.GetInt32(reader.GetOrdinal("move_id")),
                    GameId = reader.GetGuid(reader.GetOrdinal("game_id")),
                    MoveNumber = reader.GetInt32(reader.GetOrdinal("move_number")),
                    Color = reader.GetString(reader.GetOrdinal("color")),
                    FromSquare = reader.GetString(reader.GetOrdinal("from_square")),
                    ToSquare = reader.GetString(reader.GetOrdinal("to_square")),
                    Promotion = reader.IsDBNull(reader.GetOrdinal("promotion"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("promotion")),
                    SanNotation = reader.IsDBNull(reader.GetOrdinal("san_notation"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("san_notation")),
                    FenAfter = reader.GetString(reader.GetOrdinal("fen_after")),
                    TimeRemainingMs = reader.IsDBNull(reader.GetOrdinal("time_remaining_ms"))
                        ? null
                        : reader.GetInt32(reader.GetOrdinal("time_remaining_ms")),
                    MoveTimeMs = reader.IsDBNull(reader.GetOrdinal("move_time_ms"))
                        ? null
                        : reader.GetInt32(reader.GetOrdinal("move_time_ms")),
                    CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at"))
                });
            }

            return moves;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get moves for game: {GameId}", gameId);
            throw;
        }
    }

    #endregion

    #region Disposal

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await _dataSource.DisposeAsync();
        _logger.LogInformation("DatabaseService disposed");
    }

    #endregion
}

#region Record Types

/// <summary>
/// Represents a player record from the database.
/// </summary>
public sealed class PlayerRecord
{
    public Guid PlayerId { get; init; }
    public required string Username { get; init; }
    public required string Email { get; init; }
    public string? PasswordHash { get; init; }
    public int Rating { get; init; }
    public int GamesPlayed { get; init; }
    public int GamesWon { get; init; }
    public int GamesLost { get; init; }
    public int GamesDrawn { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastLoginAt { get; init; }
    public bool IsBanned { get; init; }
    public string? BanReason { get; init; }
}

/// <summary>
/// Represents a session record from the database.
/// </summary>
public sealed class SessionRecord
{
    public Guid SessionId { get; init; }
    public Guid PlayerId { get; init; }
    public required string TokenHash { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime LastActivity { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public bool IsRevoked { get; init; }
    public string? RevokedReason { get; init; }
}

/// <summary>
/// Represents a game record from the database.
/// </summary>
public sealed class GameRecord
{
    public Guid GameId { get; init; }
    public Guid WhitePlayerId { get; init; }
    public Guid BlackPlayerId { get; init; }
    public required string Status { get; init; }
    public string? Result { get; init; }
    public string? EndReason { get; init; }
    public required string TimeControl { get; init; }
    public int InitialTimeMs { get; init; }
    public int IncrementMs { get; init; }
    public string? Pgn { get; init; }
    public string? FinalFen { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public int? WhiteRatingBefore { get; init; }
    public int? BlackRatingBefore { get; init; }
    public int? WhiteRatingChange { get; init; }
    public int? BlackRatingChange { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Represents a game move record from the database.
/// </summary>
public sealed class GameMoveRecord
{
    public int MoveId { get; init; }
    public Guid GameId { get; init; }
    public int MoveNumber { get; init; }
    public required string Color { get; init; }
    public required string FromSquare { get; init; }
    public required string ToSquare { get; init; }
    public string? Promotion { get; init; }
    public string? SanNotation { get; init; }
    public required string FenAfter { get; init; }
    public int? TimeRemainingMs { get; init; }
    public int? MoveTimeMs { get; init; }
    public DateTime CreatedAt { get; init; }
}

#endregion
