using System.Net.Sockets;
using System.Text;
using ChessCore.Network;
using Microsoft.Extensions.Logging;

namespace PrometheusServer.Core;

/// <summary>
/// Represents a single client connection to the server.
/// Handles low-level TCP communication and message serialization.
/// </summary>
public sealed class ClientConnection : IDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();

    private bool _disposed;

    /// <summary>
    /// Unique identifier for this connection
    /// </summary>
    public string ConnectionId { get; }

    /// <summary>
    /// The authenticated player's ID (null if not authenticated)
    /// </summary>
    public string? PlayerId { get; private set; }

    /// <summary>
    /// The authenticated player's username (null if not authenticated)
    /// </summary>
    public string? Username { get; private set; }

    /// <summary>
    /// Whether the connection is currently active
    /// </summary>
    public bool IsConnected
    {
        get
        {
            lock (_stateLock)
            {
                return !_disposed && _tcpClient.Connected;
            }
        }
    }

    /// <summary>
    /// Time of the last activity on this connection
    /// </summary>
    public DateTime LastActivity { get; private set; }

    /// <summary>
    /// Time when the connection was established
    /// </summary>
    public DateTime ConnectedAt { get; }

    /// <summary>
    /// Remote endpoint address
    /// </summary>
    public string RemoteEndpoint { get; }

    /// <summary>
    /// Current game ID the player is in (null if not in a game)
    /// </summary>
    public string? CurrentGameId { get; set; }

    /// <summary>
    /// Creates a new ClientConnection
    /// </summary>
    /// <param name="tcpClient">The underlying TCP client</param>
    /// <param name="logger">Logger instance</param>
    public ClientConnection(TcpClient tcpClient, ILogger logger)
    {
        _tcpClient = tcpClient ?? throw new ArgumentNullException(nameof(tcpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ConnectionId = Guid.NewGuid().ToString("N")[..16];
        ConnectedAt = DateTime.UtcNow;
        LastActivity = DateTime.UtcNow;
        RemoteEndpoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "Unknown";

        // Configure TCP client for better performance
        // By default, TCP waits to fill a buffer before sending(Nagle's Algorithm) to be bandwidth-efficient
        // disable Nagle's Algorithm to force the kernel to send packets immediately, reducing latency for real - time play.
        _tcpClient.NoDelay = true;
        _tcpClient.ReceiveTimeout = 0; // No timeout, we handle this ourselves
        _tcpClient.SendTimeout = 30000; // 30 second send timeout

        _stream = _tcpClient.GetStream();
        _reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(_stream, Encoding.UTF8, leaveOpen: true)
        {
            AutoFlush = true
        };
    }

    /// <summary>
    /// Sets the authenticated player information
    /// </summary>
    /// <param name="playerId">The player's unique identifier</param>
    /// <param name="username">The player's username</param>
    public void SetPlayerId(string playerId, string username)
    {
        lock (_stateLock)
        {
            PlayerId = playerId;
            Username = username;
        }
    }

    /// <summary>
    /// Clears the authenticated player information (logout)
    /// </summary>
    public void ClearPlayerId()
    {
        lock (_stateLock)
        {
            PlayerId = null;
            Username = null;
            CurrentGameId = null;
        }
    }

    /// <summary>
    /// Updates the last activity timestamp
    /// </summary>
    public void UpdateLastActivity()
    {
        LastActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Sends a message to the client
    /// </summary>
    /// <param name="message">The message to send</param>
    /// <returns>True if the message was sent successfully</returns>
    public async Task<bool> SendMessageAsync(NetworkMessage message)
    {
        if (!IsConnected)
        {
            _logger.LogWarning($"Cannot send message - connection {ConnectionId} is not connected");
            return false;
        }

        await _sendLock.WaitAsync();
        try
        {
            var json = message.ToJson();
            await _writer.WriteLineAsync(json);
            await _writer.FlushAsync();

            _logger.LogDebug("Sent {MessageType} to {ConnectionId}", message.Type, ConnectionId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error sending message to {ConnectionId}");
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Receives a message from the client
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The received message, or null if disconnected</returns>
    public async Task<NetworkMessage?> ReceiveMessageAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return null;
        }

        try
        {
            // Use a cancellation token to allow graceful shutdown
            var line = await _reader.ReadLineAsync(cancellationToken);

            if (string.IsNullOrEmpty(line))
            {
                // Client disconnected
                _logger.LogDebug("Client {ConnectionId} sent empty message (disconnected)", ConnectionId);
                return null;
            }

            UpdateLastActivity();

            // Parse the message
            var message = NetworkMessage.FromJson(line);

            if (message == null)
            {
                _logger.LogWarning("Failed to parse message from {ConnectionId}: {Preview}",
                    ConnectionId, line[..Math.Min(line.Length, 100)]);
                return null;
            }

            _logger.LogDebug("Received {MessageType} from {ConnectionId}", message.Type, ConnectionId);
            return message;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Receive cancelled for {ConnectionId}", ConnectionId);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogDebug("IO error receiving from {ConnectionId}: {Message}", ConnectionId, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving message from {ConnectionId}", ConnectionId);
            return null;
        }
    }

    /// <summary>
    /// Sends a message synchronously (for quick responses)
    /// </summary>
    /// <param name="message">The message to send</param>
    public void SendMessageSync(NetworkMessage message)
    {
        if (!IsConnected)
            return;

        _sendLock.Wait();
        try
        {
            var json = message.ToJson();
            _writer.WriteLine(json);
            _writer.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending sync message to {ConnectionId}", ConnectionId);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Closes the connection gracefully
    /// </summary>
    public async Task CloseAsync(string? reason = null)
    {
        if (!IsConnected)
            return;

        try
        {
            // Send a disconnect notification if possible
            if (!string.IsNullOrEmpty(reason))
            {
                await SendMessageAsync(new ErrorMessage
                {
                    Code = "DISCONNECTED",
                    Message = reason
                });
            }

            // Give the message time to send
            await Task.Delay(100);
        }
        catch
        {
            // Ignore errors during close
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>
    /// Gets connection statistics
    /// </summary>
    public ConnectionStats GetStats()
    {
        return new ConnectionStats
        {
            ConnectionId = ConnectionId,
            PlayerId = PlayerId,
            Username = Username,
            RemoteEndpoint = RemoteEndpoint,
            ConnectedAt = ConnectedAt,
            LastActivity = LastActivity,
            CurrentGameId = CurrentGameId,
            IsAuthenticated = PlayerId != null,
            ConnectionDuration = DateTime.UtcNow - ConnectedAt
        };
    }

    /// <summary>
    /// Returns a string representation of the connection
    /// </summary>
    public override string ToString()
    {
        var playerInfo = PlayerId != null ? $"{Username} ({PlayerId})" : "Anonymous";
        return $"Connection {ConnectionId} from {RemoteEndpoint} - {playerInfo}";
    }

    /// <summary>
    /// Disposes the connection and releases resources
    /// </summary>
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        try
        {
            _reader.Dispose();
            _writer.Dispose();
            _stream.Dispose();
            _tcpClient.Close();
            _tcpClient.Dispose();
            _sendLock.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing connection {ConnectionId}", ConnectionId);
        }

        _logger.LogDebug("Connection {ConnectionId} disposed", ConnectionId);
    }
}

/// <summary>
/// Statistics about a client connection
/// </summary>
public sealed class ConnectionStats
{
    public required string ConnectionId { get; init; }
    public string? PlayerId { get; init; }
    public string? Username { get; init; }
    public required string RemoteEndpoint { get; init; }
    public DateTime ConnectedAt { get; init; }
    public DateTime LastActivity { get; init; }
    public string? CurrentGameId { get; init; }
    public bool IsAuthenticated { get; init; }
    public TimeSpan ConnectionDuration { get; init; }
}
