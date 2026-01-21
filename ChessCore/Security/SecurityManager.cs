using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace ChessCore.Security;

/// <summary>
/// Manages security operations including authentication, token generation, and validation.
/// </summary>
public sealed class SecurityManager
{
    private readonly string _jwtSecretKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly TimeSpan _tokenExpiration;
    private readonly SymmetricSecurityKey _signingKey;

    /// <summary>
    /// Creates a new SecurityManager with the specified configuration
    /// </summary>
    /// <param name="jwtSecretKey">Secret key for JWT signing (minimum 32 characters)</param>
    /// <param name="issuer">Token issuer identifier</param>
    /// <param name="audience">Token audience identifier</param>
    /// <param name="tokenExpirationHours">Token expiration time in hours</param>
    public SecurityManager(
        string jwtSecretKey,
        string issuer = "PrometheusServer",
        string audience = "PrometheusClient",
        int tokenExpirationHours = 24)
    {
        if (string.IsNullOrEmpty(jwtSecretKey) || jwtSecretKey.Length < 32)
            throw new ArgumentException("JWT secret key must be at least 32 characters", nameof(jwtSecretKey));

        _jwtSecretKey = jwtSecretKey;
        _issuer = issuer;
        _audience = audience;
        _tokenExpiration = TimeSpan.FromHours(tokenExpirationHours);
        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecretKey));
    }

    #region Password Hashing

    /// <summary>
    /// Hashes a password using BCrypt with a work factor of 12
    /// </summary>
    /// <param name="password">The plain text password</param>
    /// <returns>The hashed password</returns>
    public static string HashPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password cannot be empty", nameof(password));

        return BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
    }

    /// <summary>
    /// Verifies a password against a stored hash
    /// </summary>
    /// <param name="password">The plain text password to verify</param>
    /// <param name="storedHash">The stored password hash</param>
    /// <returns>True if the password matches, false otherwise</returns>
    public static bool VerifyPassword(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash))
            return false;

        try
        {
            return BCrypt.Net.BCrypt.Verify(password, storedHash);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Hashes a password on the client side before sending to server
    /// This provides an extra layer of protection during transmission
    /// Note: Server should still hash this value again before storing
    /// </summary>
    /// <param name="password">The plain text password</param>
    /// <param name="salt">A salt value (e.g., username)</param>
    /// <returns>SHA-256 hash of the salted password</returns>
    public static string ClientSideHash(string password, string salt)
    {
        using var sha256 = SHA256.Create();
        var combined = $"{salt}:{password}";
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Simple client-side password hash for transmission to server.
    /// Uses SHA-256 to hash the password before sending over the network.
    /// </summary>
    /// <param name="password">The plain text password</param>
    /// <returns>SHA-256 hash of the password as a hex string</returns>
    public static string HashPasswordClientSide(string password)
    {
        if (string.IsNullOrEmpty(password))
            return string.Empty;

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    #endregion

    #region JWT Token Management

    /// <summary>
    /// Generates a JWT token for an authenticated player
    /// </summary>
    /// <param name="playerId">The player's unique identifier</param>
    /// <param name="username">The player's username</param>
    /// <param name="additionalClaims">Additional claims to include in the token</param>
    /// <returns>A signed JWT token string</returns>
    public string GenerateToken(string playerId, string username, Dictionary<string, string>? additionalClaims = null)
    {
        var claims = new List<Claim>
        {
            new(Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.Sub, playerId),
            new(Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.UniqueName, username),
            new(Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new("playerId", playerId),
            new("username", username)
        };

        if (additionalClaims != null)
        {
            foreach (var claim in additionalClaims)
            {
                claims.Add(new Claim(claim.Key, claim.Value));
            }
        }

        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: DateTime.UtcNow.Add(_tokenExpiration),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Validates a JWT token and returns the claims if valid
    /// </summary>
    /// <param name="token">The JWT token to validate</param>
    /// <returns>TokenValidationResult containing validation status and claims</returns>
    public TokenValidationResult ValidateToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return new TokenValidationResult
            {
                IsValid = false,
                ErrorMessage = "Token is empty"
            };
        }

        var tokenHandler = new JwtSecurityTokenHandler();

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _signingKey,
            ValidateIssuer = true,
            ValidIssuer = _issuer,
            ValidateAudience = true,
            ValidAudience = _audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(5) // Allow 5 minutes of clock drift
        };

        try
        {
            var principal = tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

            if (validatedToken is not JwtSecurityToken jwtToken ||
                !jwtToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.InvariantCultureIgnoreCase))
            {
                return new TokenValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Invalid token algorithm"
                };
            }

            var playerId = principal.FindFirst("playerId")?.Value;
            var username = principal.FindFirst("username")?.Value;

            return new TokenValidationResult
            {
                IsValid = true,
                PlayerId = playerId,
                Username = username,
                Claims = principal.Claims.ToDictionary(c => c.Type, c => c.Value),
                Expiration = jwtToken.ValidTo
            };
        }
        catch (SecurityTokenExpiredException)
        {
            return new TokenValidationResult
            {
                IsValid = false,
                ErrorMessage = "Token has expired"
            };
        }
        catch (SecurityTokenException ex)
        {
            return new TokenValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Token validation failed: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new TokenValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Unexpected error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Extracts claims from a token without validating (useful for expired token inspection)
    /// </summary>
    /// <param name="token">The JWT token</param>
    /// <returns>Dictionary of claims, or null if token is invalid</returns>
    public static Dictionary<string, string>? ExtractClaimsWithoutValidation(string token)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var jwtToken = tokenHandler.ReadJwtToken(token);
            return jwtToken.Claims.ToDictionary(c => c.Type, c => c.Value);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the expiration time of a token without validating
    /// </summary>
    /// <param name="token">The JWT token</param>
    /// <returns>Expiration DateTime, or null if token is invalid</returns>
    public static DateTime? GetTokenExpiration(string token)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var jwtToken = tokenHandler.ReadJwtToken(token);
            return jwtToken.ValidTo;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if a token is expired
    /// </summary>
    public static bool IsTokenExpired(string token)
    {
        var expiration = GetTokenExpiration(token);
        return expiration == null || expiration.Value < DateTime.UtcNow;
    }

    #endregion

    #region Session Token Generation

    /// <summary>
    /// Generates a secure random session token
    /// </summary>
    /// <param name="length">Length of the token in bytes (will be base64 encoded)</param>
    /// <returns>A cryptographically secure random token</returns>
    public static string GenerateSessionToken(int length = 32)
    {
        var randomBytes = new byte[length];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes)
            .Replace("/", "_")
            .Replace("+", "-")
            .TrimEnd('=');
    }

    /// <summary>
    /// Generates a unique player ID
    /// </summary>
    /// <returns>A unique player identifier (GUID format)</returns>
    public static string GeneratePlayerId()
    {
        return Guid.NewGuid().ToString();
    }

    /// <summary>
    /// Generates a unique game ID
    /// </summary>
    /// <returns>A unique game identifier</returns>
    public static string GenerateGameId()
    {
        var bytes = new byte[9];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("/", "_")
            .Replace("+", "-")
            .TrimEnd('=');
    }

    #endregion

    #region Rate Limiting

    /// <summary>
    /// Simple in-memory rate limiter for request throttling
    /// </summary>
    private readonly Dictionary<string, RateLimitInfo> _rateLimits = new();
    private readonly object _rateLimitLock = new();

    /// <summary>
    /// Checks if a request should be rate limited
    /// </summary>
    /// <param name="key">Identifier for rate limiting (e.g., IP address, player ID)</param>
    /// <param name="maxRequests">Maximum requests allowed in the window</param>
    /// <param name="windowSeconds">Time window in seconds</param>
    /// <returns>True if the request should be allowed, false if rate limited</returns>
    public bool CheckRateLimit(string key, int maxRequests = 100, int windowSeconds = 60)
    {
        lock (_rateLimitLock)
        {
            var now = DateTime.UtcNow;

            if (!_rateLimits.TryGetValue(key, out var info))
            {
                info = new RateLimitInfo
                {
                    WindowStart = now,
                    RequestCount = 0
                };
                _rateLimits[key] = info;
            }

            // Reset window if expired
            if ((now - info.WindowStart).TotalSeconds >= windowSeconds)
            {
                info.WindowStart = now;
                info.RequestCount = 0;
            }

            // Check limit
            if (info.RequestCount >= maxRequests)
            {
                return false;
            }

            info.RequestCount++;
            return true;
        }
    }

    /// <summary>
    /// Cleans up expired rate limit entries
    /// </summary>
    public void CleanupRateLimits(int maxAgeSeconds = 300)
    {
        lock (_rateLimitLock)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-maxAgeSeconds);
            var keysToRemove = _rateLimits
                .Where(kvp => kvp.Value.WindowStart < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _rateLimits.Remove(key);
            }
        }
    }

    private class RateLimitInfo
    {
        public DateTime WindowStart { get; set; }
        public int RequestCount { get; set; }
    }

    #endregion

    #region Input Validation

    /// <summary>
    /// Validates a username
    /// </summary>
    /// <param name="username">The username to validate</param>
    /// <returns>Validation result with error message if invalid</returns>
    public static ValidationResult ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return ValidationResult.Invalid("Username is required");

        if (username.Length < 3)
            return ValidationResult.Invalid("Username must be at least 3 characters");

        if (username.Length > 20)
            return ValidationResult.Invalid("Username must be at most 20 characters");

        if (!username.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return ValidationResult.Invalid("Username can only contain letters, numbers, and underscores");

        if (char.IsDigit(username[0]))
            return ValidationResult.Invalid("Username cannot start with a number");

        // Check for forbidden usernames
        var forbidden = new[] { "admin", "moderator", "system", "server", "null", "undefined", "lexa", "helsie", "joni" };
        if (forbidden.Contains(username.ToLower()))
            return ValidationResult.Invalid("This username is not allowed");

        return ValidationResult.Valid();
    }

    /// <summary>
    /// Validates an email address
    /// </summary>
    /// <param name="email">The email to validate</param>
    /// <returns>Validation result with error message if invalid</returns>
    public static ValidationResult ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return ValidationResult.Invalid("Email is required");

        if (email.Length > 254)
            return ValidationResult.Invalid("Email is too long");

        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            if (addr.Address != email)
                return ValidationResult.Invalid("Invalid email format");
        }
        catch
        {
            return ValidationResult.Invalid("Invalid email format");
        }

        return ValidationResult.Valid();
    }

    /// <summary>
    /// Validates a password
    /// </summary>
    /// <param name="password">The password to validate</param>
    /// <returns>Validation result with error message if invalid</returns>
    public static ValidationResult ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            return ValidationResult.Invalid("Password is required");

        if (password.Length < 8)
            return ValidationResult.Invalid("Password must be at least 8 characters");

        if (password.Length > 128)
            return ValidationResult.Invalid("Password is too long");

        if (!password.Any(char.IsUpper))
            return ValidationResult.Invalid("Password must contain at least one uppercase letter");

        if (!password.Any(char.IsLower))
            return ValidationResult.Invalid("Password must contain at least one lowercase letter");

        if (!password.Any(char.IsDigit))
            return ValidationResult.Invalid("Password must contain at least one number");

        return ValidationResult.Valid();
    }

    /// <summary>
    /// Sanitizes a string input to prevent injection attacks
    /// </summary>
    /// <param name="input">The input string</param>
    /// <param name="maxLength">Maximum allowed length</param>
    /// <returns>Sanitized string</returns>
    public static string SanitizeInput(string? input, int maxLength = 1000)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        // Truncate to max length
        if (input.Length > maxLength)
            input = input[..maxLength];

        // Remove control characters
        input = new string(input.Where(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t').ToArray());

        // Trim whitespace
        return input.Trim();
    }

    #endregion
}

/// <summary>
/// Result of token validation
/// </summary>
public sealed class TokenValidationResult
{
    public bool IsValid { get; init; }
    public string? PlayerId { get; init; }
    public string? Username { get; init; }
    public Dictionary<string, string>? Claims { get; init; }
    public DateTime? Expiration { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of input validation
/// </summary>
public readonly struct ValidationResult
{
    public bool IsValid { get; }
    public string? ErrorMessage { get; }

    private ValidationResult(bool isValid, string? errorMessage)
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
    }

    public static ValidationResult Valid() => new(true, null);
    public static ValidationResult Invalid(string message) => new(false, message);
}
