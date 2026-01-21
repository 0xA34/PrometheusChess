using System.Collections.Concurrent;
using ChessCore.Models;
using PrometheusServer.Logging;
using Microsoft.Extensions.Logging;

namespace PrometheusServer.Services;

/// <summary>
/// Service for matching players together based on rating and preferences.
/// Implements a rating-based matchmaking queue with expanding search radius.
/// Note that this is just a simple matchmaking, I am not really into the whole advanced concept of this
/// Usually this is just the elo based stuff, however, if you want to expand, there are a lot more patterns you should add.
/// </summary>
public sealed class MatchmakingService : IDisposable
{
    private readonly ILogger<MatchmakingService> _logger;
    private readonly ConcurrentDictionary<string, MatchmakingRequest> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _matchmakingTask;

    private readonly int _defaultRatingRange;
    private readonly int _maxRatingRange;
    private readonly int _ratingExpansionIntervalSeconds;
    private readonly int _ratingExpansionAmount;

    /// <summary>
    /// Event raised when a match is found
    /// </summary>
    public event EventHandler<MatchFoundEventArgs>? MatchFound;

    /// <summary>
    /// Number of players currently in the queue
    /// </summary>
    public int PlayersInQueue => _queue.Count;

    /// <summary>
    /// Creates a new MatchmakingService
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="defaultRatingRange">Default rating range for matching (+/-)</param>
    /// <param name="maxRatingRange">Maximum rating range after expansion</param>
    /// <param name="ratingExpansionIntervalSeconds">Seconds between rating range expansions</param>
    /// <param name="ratingExpansionAmount">Amount to expand rating range each interval</param>
    public MatchmakingService(
        ILogger<MatchmakingService> logger,
        int defaultRatingRange = 200,
        int maxRatingRange = 500,
        int ratingExpansionIntervalSeconds = 30,
        int ratingExpansionAmount = 50)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _defaultRatingRange = defaultRatingRange;
        _maxRatingRange = maxRatingRange;
        _ratingExpansionIntervalSeconds = ratingExpansionIntervalSeconds;
        _ratingExpansionAmount = ratingExpansionAmount;

        // Start the matchmaking loop
        _matchmakingTask = Task.Run(() => MatchmakingLoopAsync(_cts.Token));

        _logger.LogDebug("Matchmaking service started (rating range: {Min}-{Max}, expansion: {Expansion}/sec)",
            _defaultRatingRange, _maxRatingRange, _ratingExpansionAmount);
    }

    /// <summary>
    /// Adds a player to the matchmaking queue
    /// </summary>
    /// <param name="request">The matchmaking request</param>
    public void EnqueuePlayer(MatchmakingRequest request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        // Remove any existing entry for this player
        _queue.TryRemove(request.PlayerId, out _);

        // Set the queue entry time
        request.QueuedAt = DateTime.UtcNow;
        request.CurrentRatingRange = request.RatingRange > 0 ? request.RatingRange : _defaultRatingRange;

        if (_queue.TryAdd(request.PlayerId, request))
        {
            _logger.LogDebug("Player {Username} ({Rating}) added to matchmaking queue for {TimeControl}",
                request.Username, request.Rating, request.TimeControl);
        }
        else
        {
            _logger.LogWarning("Failed to add player {Username} to matchmaking queue", request.Username);
        }
    }

    /// <summary>
    /// Removes a player from the matchmaking queue
    /// </summary>
    /// <param name="playerId">The player's ID</param>
    /// <returns>True if the player was removed</returns>
    public bool DequeuePlayer(string playerId)
    {
        if (_queue.TryRemove(playerId, out var request))
        {
            _logger.LogDebug("Player {Username} removed from matchmaking queue", request.Username);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if a player is in the queue
    /// </summary>
    /// <param name="playerId">The player's ID</param>
    /// <returns>True if the player is in the queue</returns>
    public bool IsInQueue(string playerId)
    {
        return _queue.ContainsKey(playerId);
    }

    /// <summary>
    /// Gets the queue position for a player
    /// </summary>
    /// <param name="playerId">The player's ID</param>
    /// <returns>Queue position (1-based) or -1 if not in queue</returns>
    public int GetQueuePosition(string playerId)
    {
        if (!_queue.TryGetValue(playerId, out var request))
            return -1;

        // Count players who queued before this one
        return _queue.Values.Count(r => r.QueuedAt < request.QueuedAt) + 1;
    }

    /// <summary>
    /// Gets estimated wait time for a player
    /// </summary>
    /// <param name="playerId">The player's ID</param>
    /// <returns>Estimated wait time in seconds, or -1 if unknown</returns>
    public int GetEstimatedWaitTime(string playerId)
    {
        if (!_queue.TryGetValue(playerId, out var request))
            return -1;

        // Simple estimate based on queue size and average match time
        var position = GetQueuePosition(playerId);
        var similarPlayers = _queue.Values.Count(r =>
            r.TimeControl == request.TimeControl &&
            Math.Abs(r.Rating - request.Rating) <= _maxRatingRange);

        if (similarPlayers <= 1)
            return 60; // Default estimate if alone

        return Math.Max(5, 30 / similarPlayers * position);
    }

    /// <summary>
    /// Main matchmaking loop that continuously tries to match players
    /// </summary>
    private async Task MatchmakingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct); // Check every second

                if (_queue.IsEmpty)
                    continue;

                // Expand rating ranges for players who have been waiting
                ExpandRatingRanges();

                // Try to find matches
                var matched = TryFindMatches();

                foreach (var match in matched)
                {
                    // Remove matched players from queue
                    _queue.TryRemove(match.Player1.PlayerId, out _);
                    _queue.TryRemove(match.Player2.PlayerId, out _);

                    _logger.LogDebug("Match created: {Player1} ({Rating1}) vs {Player2} ({Rating2}) - {TimeControl}",
                        match.Player1.Username, match.Player1.Rating,
                        match.Player2.Username, match.Player2.Rating,
                        match.TimeControl);

                    // Raise event
                    OnMatchFound(match);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in matchmaking loop");
            }
        }
    }

    /// <summary>
    /// Expands rating ranges for players who have been waiting
    /// </summary>
    private void ExpandRatingRanges()
    {
        var now = DateTime.UtcNow;

        foreach (var request in _queue.Values)
        {
            var waitTime = now - request.QueuedAt;
            var expansions = (int)(waitTime.TotalSeconds / _ratingExpansionIntervalSeconds);

            if (expansions > 0)
            {
                var newRange = request.RatingRange + (expansions * _ratingExpansionAmount);
                request.CurrentRatingRange = Math.Min(newRange, _maxRatingRange);
            }
        }
    }

    /// <summary>
    /// Tries to find valid matches among queued players
    /// </summary>
    /// <returns>List of match results</returns>
    private List<MatchFoundEventArgs> TryFindMatches()
    {
        var matches = new List<MatchFoundEventArgs>();
        var matchedPlayerIds = new HashSet<string>();

        // Group by time control
        var groups = _queue.Values
            .Where(r => !matchedPlayerIds.Contains(r.PlayerId))
            .GroupBy(r => r.TimeControl);

        foreach (var group in groups)
        {
            var players = group
                .OrderBy(r => r.QueuedAt) // Priority to longer waiting players
                .ToList();

            for (int i = 0; i < players.Count; i++)
            {
                if (matchedPlayerIds.Contains(players[i].PlayerId))
                    continue;

                var player1 = players[i];

                // Find best match for this player
                MatchmakingRequest? bestMatch = null;
                int bestRatingDiff = int.MaxValue;

                for (int j = i + 1; j < players.Count; j++)
                {
                    if (matchedPlayerIds.Contains(players[j].PlayerId))
                        continue;

                    var player2 = players[j];

                    // Check if they can match each other
                    if (!CanMatch(player1, player2))
                        continue;

                    var ratingDiff = Math.Abs(player1.Rating - player2.Rating);
                    if (ratingDiff < bestRatingDiff)
                    {
                        bestMatch = player2;
                        bestRatingDiff = ratingDiff;
                    }
                }

                if (bestMatch != null)
                {
                    matchedPlayerIds.Add(player1.PlayerId);
                    matchedPlayerIds.Add(bestMatch.PlayerId);

                    // Randomly assign colors (or use some other criteria)
                    var (white, black) = AssignColors(player1, bestMatch);

                    matches.Add(new MatchFoundEventArgs
                    {
                        Player1 = new MatchedPlayer
                        {
                            PlayerId = white.PlayerId,
                            Username = white.Username,
                            Rating = white.Rating,
                            ConnectionId = white.ConnectionId
                        },
                        Player2 = new MatchedPlayer
                        {
                            PlayerId = black.PlayerId,
                            Username = black.Username,
                            Rating = black.Rating,
                            ConnectionId = black.ConnectionId
                        },
                        TimeControl = player1.TimeControl,
                        InitialTimeMs = player1.InitialTimeMs,
                        IncrementMs = player1.IncrementMs
                    });
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Checks if two players can be matched
    /// </summary>
    private bool CanMatch(MatchmakingRequest player1, MatchmakingRequest player2)
    {
        // Must have same time control
        if (player1.TimeControl != player2.TimeControl)
            return false;

        // Check rating range (both must accept each other)
        var ratingDiff = Math.Abs(player1.Rating - player2.Rating);

        return ratingDiff <= player1.CurrentRatingRange &&
               ratingDiff <= player2.CurrentRatingRange;
    }

    /// <summary>
    /// Assigns colors to matched players
    /// </summary>
    private (MatchmakingRequest White, MatchmakingRequest Black) AssignColors(
        MatchmakingRequest player1, MatchmakingRequest player2)
    {
        // Simple random assignment
        // Could be enhanced to consider player preferences or history
        if (Random.Shared.Next(2) == 0)
            return (player1, player2);
        else
            return (player2, player1);
    }

    /// <summary>
    /// Raises the MatchFound event
    /// </summary>
    private void OnMatchFound(MatchFoundEventArgs e)
    {
        MatchFound?.Invoke(this, e);
    }

    /// <summary>
    /// Gets queue statistics
    /// </summary>
    public QueueStatistics GetStatistics()
    {
        var players = _queue.Values.ToList();
        var now = DateTime.UtcNow;

        return new QueueStatistics
        {
            TotalPlayersInQueue = players.Count,
            PlayersByTimeControl = players.GroupBy(p => p.TimeControl)
                .ToDictionary(g => g.Key, g => g.Count()),
            AverageRating = players.Count > 0 ? (int)players.Average(p => p.Rating) : 0,
            AverageWaitTimeSeconds = players.Count > 0
                ? (int)players.Average(p => (now - p.QueuedAt).TotalSeconds)
                : 0,
            LongestWaitTimeSeconds = players.Count > 0
                ? (int)players.Max(p => (now - p.QueuedAt).TotalSeconds)
                : 0
        };
    }

    /// <summary>
    /// Disposes the matchmaking service
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            _matchmakingTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Ignore
        }

        _cts.Dispose();
        _queue.Clear();

        _logger.LogDebug("Matchmaking service stopped");
    }
}

/// <summary>
/// Request to join the matchmaking queue
/// </summary>
public sealed class MatchmakingRequest
{
    public required string PlayerId { get; set; }
    public required string Username { get; set; }
    public int Rating { get; set; }
    public TimeControlType TimeControl { get; set; } = TimeControlType.Rapid;
    public int InitialTimeMs { get; set; } = 600000;
    public int IncrementMs { get; set; } = 0;
    public int RatingRange { get; set; } = 200;
    public int CurrentRatingRange { get; set; } = 200;
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public string? ConnectionId { get; set; }
}

/// <summary>
/// Information about a matched player
/// </summary>
public sealed class MatchedPlayer
{
    public required string PlayerId { get; init; }
    public required string Username { get; init; }
    public int Rating { get; init; }
    public string? ConnectionId { get; init; }
}

/// <summary>
/// Event arguments for when a match is found
/// </summary>
public sealed class MatchFoundEventArgs : EventArgs
{
    /// <summary>
    /// First player (will be assigned white)
    /// </summary>
    public required MatchedPlayer Player1 { get; init; }

    /// <summary>
    /// Second player (will be assigned black)
    /// </summary>
    public required MatchedPlayer Player2 { get; init; }

    /// <summary>
    /// Time control for the match
    /// </summary>
    public TimeControlType TimeControl { get; init; }

    /// <summary>
    /// Initial time in milliseconds
    /// </summary>
    public int InitialTimeMs { get; init; }

    /// <summary>
    /// Increment in milliseconds
    /// </summary>
    public int IncrementMs { get; init; }
}

/// <summary>
/// Statistics about the matchmaking queue
/// </summary>
public sealed class QueueStatistics
{
    public int TotalPlayersInQueue { get; init; }
    public Dictionary<TimeControlType, int> PlayersByTimeControl { get; init; } = new();
    public int AverageRating { get; init; }
    public int AverageWaitTimeSeconds { get; init; }
    public int LongestWaitTimeSeconds { get; init; }
}
