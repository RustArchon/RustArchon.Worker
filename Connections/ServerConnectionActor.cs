// Copyright ©2026 Scott Blomfield

using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Messaging.Contracts;
using RustArchon.Rcon;
using RustArchon.Rcon.Entities;
using RustArchon.Rcon.EventArgs;
using RustArchon.Rcon.KillFeed;
using RustArchon.Rcon.PlayerEvents;
using RustArchon.Worker.Configuration;
using System.Text.Json;

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

    // Confirmed live: Rust's playerlist JSON uses "SteamID"/"OwnerSteamID" (capital ID), but
    // RustArchon.Rcon's Player class - matching the rest of this codebase's naming - has
    // SteamId/OwnerSteamId. System.Text.Json's default deserialization is case-sensitive, so without
    // this, every Player.SteamId silently comes back "" (the field's default), which broke
    // ReconcilePlayerListAsync's SteamId-keyed diff in a nasty way: it read as "nobody is online" on
    // every poll regardless of who actually was, which - since diffing against an empty set makes
    // everyone in _lastKnownPlayers look newly disconnected - spuriously disconnected every real
    // player shortly after they connected. Caught live: a player who joined and stayed online got
    // marked disconnected in PlayerSession ~45s later, right on the next poll.
    private static readonly JsonSerializerOptions PlayerListJsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Confirmed live against a real production server: Rust's console emits an immediate, reliable
    // "<ip>:<port>/<steamId>/<name> joined [...]" / "... disconnecting: <reason>" line for every
    // connect/disconnect (see PlayerConnectionTextParser's remarks) - OnRawMessageReceived below
    // parses these the moment they arrive, the same way it already does for the kill feed, so that's
    // now the primary detection path. PlayerListPollLoopAsync is kept as a slower reconciliation
    // safety net (catches a missed/garbled line, or - the one case text parsing can never cover -
    // everyone already online when this actor starts, since there's no console line for "was already
    // here"), not the thing detection latency depends on, hence the more relaxed interval.
    private static readonly TimeSpan PlayerListPollInterval = TimeSpan.FromSeconds(60);

    // Drives the Stats tab's graphs (player count, network in/out, memory) - see
    // ServerInfoSnapshotCaptured's remarks for why only these fields are persisted at all. 60s matches
    // PlayerListPollInterval's cadence: frequent enough for a meaningful trend line, infrequent enough
    // that a busy tenant's snapshot table doesn't grow unreasonably fast (1,440 rows/server/day).
    private static readonly TimeSpan ServerInfoPollInterval = TimeSpan.FromSeconds(60);

    // Only the fields ServerInfoSnapshotCaptured actually carries - deliberately not the full
    // RustArchon.Rcon.Entities.ServerInfo shape. That type's GameTime/SaveCreatedTime use a
    // non-ISO date format requiring ServerInfoParser's custom DateTimeConverter (see ParserBase) to
    // deserialize; since this poll never touches those fields, leaving them off this local type
    // altogether sidesteps that entirely - System.Text.Json just ignores the extra JSON properties.
    private sealed class ServerInfoPollResult
    {
        public int Players { get; set; }
        public int MaxPlayers { get; set; }
        public int NetworkIn { get; set; }
        public int NetworkOut { get; set; }
        public int Memory { get; set; }
    }

    private readonly Guid _tenantId;
    private readonly Guid _workerId;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<ServerConnectionActor> _logger;
    private readonly RustWebRconClient _client;

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _heartbeatLoopTask;
    private readonly Task _playerListPollLoopTask;
    private readonly Task _serverInfoPollLoopTask;

    // Actor-local, not persisted - rebuilt from scratch every time this actor starts (a fresh worker
    // claim, or this same worker reconnecting after a restart). See PlayerListPollLoopAsync's remarks
    // for the one real consequence of that: everyone already online at that moment gets reported as a
    // fresh "connect" on the very first poll, since there's no prior snapshot to diff against.
    // Written from two different call paths that can run concurrently - the WebSocket client's own
    // receive loop (OnRawMessageReceived) and this actor's poll loop task - so every access goes
    // through _lastKnownPlayersLock.
    private readonly Dictionary<string, Player> _lastKnownPlayers = new();
    private readonly object _lastKnownPlayersLock = new();

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
        _client.ProcessingError += OnProcessingError;

        _heartbeatLoopTask = Task.Run(() => HeartbeatLoopAsync(_lifetimeCts.Token));
        _playerListPollLoopTask = Task.Run(() => PlayerListPollLoopAsync(_lifetimeCts.Token));
        _serverInfoPollLoopTask = Task.Run(() => ServerInfoPollLoopAsync(_lifetimeCts.Token));

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

    /// <summary>
    /// Reports an exception <see cref="RustWebRconClient"/> caught while processing an inbound frame
    /// or connection-state transition - see its <c>ProcessingError</c> remarks. This is what makes a
    /// bug in this actor's own event handlers (<see cref="OnRawMessageReceived"/>,
    /// <see cref="OnConnectionChanged"/>) visible instead of silently turning into an endless reconnect
    /// loop the way it did before that safety net existed - confirmed live as the actual root cause of
    /// one such loop.
    /// </summary>
    private void OnProcessingError(object? sender, Exception e)
    {
        _logger.LogError(e, "Error processing an inbound frame or connection-state change for server {ServerId} - the frame/transition was dropped, connection unaffected", ServerId);
    }

    private void OnConnectionChanged(object? sender, ConnectionChangedEventArgs e)
    {
        var status = e.IsConnected ? RconConnectionStatus.Connected : RconConnectionStatus.Reconnecting;

        if (e.IsConnected)
        {
            _logger.LogInformation("Connected to server {ServerId} ({Detail})", ServerId, e.Detail);
            _ = PublishStatusAsync(status, null, CancellationToken.None);
            return;
        }

        // Previously a hardcoded generic message regardless of cause, which made a genuinely stuck
        // reconnect (Websocket.Client retrying every ErrorReconnectTimeout but never succeeding, even
        // though the server itself is reachable - confirmed live via a second, unrelated RCON client
        // staying connected throughout) indistinguishable from a normal transient blip until now.
        _logger.LogWarning(e.Exception, "Lost connection to server {ServerId} ({Detail})", ServerId, e.Detail);
        var detail = e.Detail is null
            ? "Connection lost - retrying automatically"
            : $"Connection lost ({e.Detail}) - retrying automatically";
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

        // Every frame is a candidate death line - see KillFeedTextParser's remarks for why this can't
        // be narrowed to "only generic console frames" up front (the Type field's meaning isn't
        // reliably documented). Non-matches (the overwhelming majority of frames) are just a cheap
        // pair of regex misses. Only published when a player is actually involved on either side -
        // NPC-vs-NPC/environmental noise (e.g. a helicopter's own scripted kills) isn't what RustArchon
        // tracks player history for.
        if (KillFeedTextParser.TryParse(response.Message, out var kill) && kill is not null
            && (kill.VictimSteamId is not null || kill.KillerSteamId is not null))
        {
            _ = _publishEndpoint.Publish(new PlayerKilled(
                ServerId,
                _tenantId,
                DateTimeOffset.UtcNow,
                kill.VictimName,
                kill.VictimSteamId,
                kill.KillerName,
                kill.KillerSteamId,
                kill.Weapon,
                response.Message));
        }

        // Every frame is also a candidate join/leave line - see PlayerConnectionTextParser's remarks.
        // This is now the primary way ReconcilePlayerListAsync's dictionary gets updated; the poll
        // loop just double-checks it periodically rather than being the detection path itself.
        if (PlayerConnectionTextParser.TryParse(response.Message, out var connectionEvent) && connectionEvent is not null)
        {
            HandleConnectionEvent(connectionEvent);
        }
    }

    private void HandleConnectionEvent(PlayerConnectionEvent connectionEvent)
    {
        var now = DateTimeOffset.UtcNow;
        bool publishConnect;
        bool publishDisconnect;

        lock (_lastKnownPlayersLock)
        {
            if (connectionEvent.Type == PlayerConnectionEventType.Connected)
            {
                // Guards against a duplicate "joined" line (seen once already: some console spam
                // repeats under load) re-publishing a second PlayerConnected for someone already
                // tracked.
                publishConnect = !_lastKnownPlayers.ContainsKey(connectionEvent.SteamId);
                publishDisconnect = false;

                if (publishConnect)
                {
                    _lastKnownPlayers[connectionEvent.SteamId] = new Player
                    {
                        SteamId = connectionEvent.SteamId,
                        DisplayName = connectionEvent.DisplayName ?? connectionEvent.SteamId,
                        Address = connectionEvent.IpAddress ?? string.Empty
                    };
                }
            }
            else
            {
                publishDisconnect = _lastKnownPlayers.Remove(connectionEvent.SteamId);
                publishConnect = false;
            }
        }

        if (publishConnect)
        {
            _ = _publishEndpoint.Publish(new PlayerConnected(
                ServerId,
                _tenantId,
                connectionEvent.SteamId,
                connectionEvent.DisplayName ?? connectionEvent.SteamId,
                connectionEvent.IpAddress ?? string.Empty,
                now));
        }
        else if (publishDisconnect)
        {
            _ = _publishEndpoint.Publish(new PlayerDisconnected(ServerId, _tenantId, connectionEvent.SteamId, now));
        }
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

    /// <summary>
    /// Polls <c>playerlist</c> on a fixed interval and diffs the result against
    /// <see cref="_lastKnownPlayers"/> to derive any <see cref="PlayerConnected"/>/
    /// <see cref="PlayerDisconnected"/> events that text-based detection in
    /// <see cref="OnRawMessageReceived"/> missed.
    /// </summary>
    /// <remarks>
    /// No longer the primary detection path (see <see cref="PlayerListPollInterval"/>'s remarks) - the
    /// one thing this still covers that text parsing structurally can't is someone already online at
    /// the moment this actor starts: there's no console line for "was already here" to parse, so the
    /// very first poll is what discovers them (and, since there's no prior snapshot to diff against
    /// on that first poll, they appear as a fresh connect rather than "already connected").
    /// </remarks>
    private async Task PlayerListPollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PlayerListPollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            List<Player> currentPlayers;
            try
            {
                // raiseMessageReceived: false - this is RustArchon's own internal reconciliation
                // check, not something the user ran, so its response shouldn't join the server's
                // actual console/chat history (see RustWebRconClient.SendCommandAsync's remarks). A
                // user-run "playerlist" from the Console tab goes through SendCommandAsync below with
                // the default (true) and does show up, same as any other command they send.
                var response = await _client.SendCommandAsync(
                    "playerlist", DefaultCommandTimeout, cancellationToken, raiseMessageReceived: false);
                currentPlayers = JsonSerializer.Deserialize<List<Player>>(response.Message, PlayerListJsonOptions) ?? new List<Player>();
            }
            catch (InvalidOperationException)
            {
                continue; // Not connected right now - the next successful poll picks up from there.
            }
            catch (TimeoutException)
            {
                continue; // No response in time - treated the same as "not connected right now".
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Player-list poll failed for server {ServerId}", ServerId);
                continue;
            }

            try
            {
                await ReconcilePlayerListAsync(currentPlayers, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reconcile player list for server {ServerId}", ServerId);
            }
        }
    }

    private async Task ReconcilePlayerListAsync(List<Player> currentPlayers, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var currentBySteamId = currentPlayers
            .Where(p => !string.IsNullOrEmpty(p.SteamId))
            .ToDictionary(p => p.SteamId);

        List<string> newlyConnected;
        List<string> newlyDisconnected;

        // Snapshot the diff and update the dictionary under one lock, then publish outside it - this
        // is the same dictionary OnRawMessageReceived's text-based detection reads/writes concurrently
        // (see _lastKnownPlayersLock's remarks), and in the steady state this should find nothing to
        // do at all, since text detection already handled it the moment the console line arrived.
        lock (_lastKnownPlayersLock)
        {
            newlyConnected = currentBySteamId.Keys.Except(_lastKnownPlayers.Keys).ToList();
            newlyDisconnected = _lastKnownPlayers.Keys.Except(currentBySteamId.Keys).ToList();

            _lastKnownPlayers.Clear();
            foreach (var (steamId, player) in currentBySteamId)
            {
                _lastKnownPlayers[steamId] = player;
            }
        }

        foreach (var steamId in newlyConnected)
        {
            var player = currentBySteamId[steamId];
            await _publishEndpoint.Publish(
                new PlayerConnected(ServerId, _tenantId, steamId, player.DisplayName, player.Address, now),
                cancellationToken);
        }

        foreach (var steamId in newlyDisconnected)
        {
            await _publishEndpoint.Publish(
                new PlayerDisconnected(ServerId, _tenantId, steamId, now),
                cancellationToken);
        }

        // Every currently-known player, not just newly-connected ones - ping and Rust's own
        // anti-cheat violation level are only ever visible via this poll, never the console join line,
        // so this is the only way an open session's "last known" values for either ever get updated.
        foreach (var (steamId, player) in currentBySteamId)
        {
            await _publishEndpoint.Publish(
                new PlayerSessionSnapshotUpdated(ServerId, _tenantId, steamId, player.Ping, player.ViolationLevel, now),
                cancellationToken);
        }
    }

    /// <summary>
    /// Polls <c>serverinfo</c> on a fixed interval and publishes <see cref="ServerInfoSnapshotCaptured"/>
    /// with the fields worth graphing over time - see that record's remarks.
    /// </summary>
    private async Task ServerInfoPollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ServerInfoPollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            ServerInfoPollResult? info;
            try
            {
                // raiseMessageReceived: false - same reasoning as the playerlist poll above: this is
                // RustArchon's own internal telemetry capture, not something a user ran, so it
                // shouldn't join the server's actual console history.
                var response = await _client.SendCommandAsync(
                    "serverinfo", DefaultCommandTimeout, cancellationToken, raiseMessageReceived: false);
                info = JsonSerializer.Deserialize<ServerInfoPollResult>(response.Message);
            }
            catch (InvalidOperationException)
            {
                continue; // Not connected right now - the next successful poll picks up from there.
            }
            catch (TimeoutException)
            {
                continue; // No response in time - treated the same as "not connected right now".
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "serverinfo poll failed for server {ServerId}", ServerId);
                continue;
            }

            if (info is null)
            {
                continue;
            }

            try
            {
                await _publishEndpoint.Publish(
                    new ServerInfoSnapshotCaptured(
                        ServerId, _tenantId, info.Players, info.MaxPlayers, info.NetworkIn, info.NetworkOut,
                        info.Memory, DateTimeOffset.UtcNow),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish serverinfo snapshot for server {ServerId}", ServerId);
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

        try
        {
            await _playerListPollLoopTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        try
        {
            await _serverInfoPollLoopTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _client.ConnectionChanged -= OnConnectionChanged;
        _client.MessageReceived -= OnRawMessageReceived;
        _client.ProcessingError -= OnProcessingError;
        _client.Dispose();

        _lifetimeCts.Dispose();

        await PublishStatusAsync(RconConnectionStatus.Disconnected, "Connection actor stopped", CancellationToken.None);
    }
}
