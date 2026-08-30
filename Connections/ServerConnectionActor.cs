// Copyright ©2026 Scott Blomfield

using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Messaging.Contracts;
using RustArchon.Rcon;
using RustArchon.Rcon.EventArgs;
using RustArchon.Worker.Configuration;

namespace RustArchon.Worker.Connections;

/// <summary>
/// Owns one persistent WebRCON connection to a single Rust server for as long as this worker is
/// responsible for it, via <see cref="RustWebRconClient"/> (see the RustArchon.Rcon project) - this
/// class no longer talks to a raw <see cref="System.Net.WebSockets.ClientWebSocket"/> itself, or
/// implements its own reconnect loop; both now live in that library.
/// </summary>
/// <remarks>
/// Created and torn down exclusively by <see cref="IConnectionSupervisor"/> - never resolved from the
/// DI container, since one instance exists per currently-owned server, not per service type.
/// </remarks>
public sealed class ServerConnectionActor : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(8);

    private readonly Guid _tenantId;
    private readonly Guid _workerId;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<ServerConnectionActor> _logger;
    private readonly RustWebRconClient _client;

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _heartbeatLoopTask;

    public ServerConnectionActor(
        Guid serverId,
        Guid tenantId,
        string host,
        int port,
        string rconPassword,
        Guid workerId,
        IPublishEndpoint publishEndpoint,
        ReconnectOptions reconnectOptions,
        ILogger<ServerConnectionActor> logger)
    {
        ServerId = serverId;
        _tenantId = tenantId;
        _workerId = workerId;
        _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(reconnectOptions);

        _client = new RustWebRconClient(
            "RustArchon",
            host,
            port,
            rconPassword,
            reconnectTimeout: reconnectOptions.ReconnectTimeout,
            errorReconnectTimeout: reconnectOptions.ErrorReconnectTimeout);
        _client.ConnectionChanged += OnConnectionChanged;
        _client.MessageReceived += OnRawMessageReceived;

        _heartbeatLoopTask = Task.Run(() => HeartbeatLoopAsync(_lifetimeCts.Token));

        _ = PublishStatusAsync(RconConnectionStatus.Connecting, null, CancellationToken.None);
        if (!_client.Connect())
        {
            // Not fatal - Websocket.Client keeps retrying on its own regardless of whether the very
            // first attempt threw synchronously. See RustWebRconClient.Connect's remarks.
            _logger.LogWarning("Initial connection attempt to server {ServerId} did not start cleanly; the client will keep retrying on its own", serverId);
        }
    }

    public Guid ServerId { get; }

    /// <summary>
    /// Sends a command over the live connection and awaits its response.
    /// </summary>
    /// <returns>
    /// A result with <c>Error = "NotConnected"</c> if the socket isn't open right now, or
    /// <c>Error = "Timeout"</c> if no response arrives within <paramref name="timeout"/>.
    /// </returns>
    public async Task<RconCommandResult> SendCommandAsync(string command, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.SendCommandAsync(command, timeout ?? DefaultCommandTimeout, cancellationToken);
            return new RconCommandResult(true, response.Message, response.Type, response.Stacktrace, null);
        }
        catch (InvalidOperationException)
        {
            return new RconCommandResult(false, null, null, null, "NotConnected");
        }
        catch (TimeoutException)
        {
            return new RconCommandResult(false, null, null, null, "Timeout");
        }
    }

    private void OnConnectionChanged(object? sender, ConnectionChangedEventArgs e)
    {
        var status = e.IsConnected ? RconConnectionStatus.Connected : RconConnectionStatus.Reconnecting;
        var detail = e.IsConnected ? null : "Connection lost - retrying automatically";
        _ = PublishStatusAsync(status, detail, CancellationToken.None);
    }

    private void OnRawMessageReceived(object? sender, MessageReceivedEventArgs e)
    {
        var response = e.Response;
        _ = _publishEndpoint.Publish(new RconFrameCaptured(
            ServerId,
            _tenantId,
            DateTimeOffset.UtcNow,
            response.Identifier,
            response.Type,
            response.Message,
            response.Stacktrace));
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        // Unconditional for as long as this actor is alive - unlike the previous raw-socket
        // implementation, there's no in-process reconnect loop of our own left to hang, since
        // RustWebRconClient/Websocket.Client own reconnection entirely now. The one residual gap this
        // doesn't catch: Websocket.Client's own internal retry loop silently dying without surfacing
        // an error - there's no public API on the client to detect that specifically, so it's an
        // accepted limitation rather than something this loop can check for.
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await _publishEndpoint.Publish(
                    new ServerConnectionHeartbeat(ServerId, _workerId, DateTimeOffset.UtcNow),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish heartbeat for server {ServerId}", ServerId);
            }
        }
    }

    private async Task PublishStatusAsync(RconConnectionStatus status, string? detail, CancellationToken cancellationToken)
    {
        try
        {
            await _publishEndpoint.Publish(
                new ConnectionStatusChanged(ServerId, _tenantId, status, detail, DateTimeOffset.UtcNow),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish connection status for server {ServerId}", ServerId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetimeCts.CancelAsync();

        try
        {
            await _heartbeatLoopTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _client.ConnectionChanged -= OnConnectionChanged;
        _client.MessageReceived -= OnRawMessageReceived;
        _client.Dispose();

        _lifetimeCts.Dispose();

        await PublishStatusAsync(RconConnectionStatus.Disconnected, "Connection actor stopped", CancellationToken.None);
    }
}
