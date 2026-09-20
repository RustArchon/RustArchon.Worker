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

    // Drives the Stats tab's graphs (player count, network in/out, memory, framerate) - see
    // ServerInfoSnapshotCaptured's remarks for why only these fields are persisted at all. 60s matches
    // PlayerListPollInterval's cadence: frequent enough for a meaningful trend line, infrequent enough
    // that a busy tenant's snapshot table doesn't grow unreasonably fast (1,440 rows/server/day).
    private static readonly TimeSpan ServerInfoPollInterval = TimeSpan.FromSeconds(60);

    // Keeps the Plugins tab's ServerPlugin rows current - see ServerPluginsCaptured's remarks. The
    // plugin set only changes when an admin loads/unloads/updates one, so unlike the two polls above a
    // several-minute interval is plenty; the short first delay just gives a freshly-claimed server time
    // to finish connecting so the tab isn't empty for a whole interval, and a failed attempt (not
    // connected yet, no reply in time) retries on the short one rather than waiting out a full interval.
    private static readonly TimeSpan PluginListPollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PluginListInitialDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PluginListRetryInterval = TimeSpan.FromSeconds(30);

    // How often the RustArchon plugin's combat events are collected. The plugin records continuously into a bounded
    // buffer whatever this does, so this only sets how promptly the Panel sees them and how big each batch is - a
    // Worker that is away for a while loses nothing until the plugin's buffer wraps (and is told when it did).
    private static readonly TimeSpan CombatDrainInterval = TimeSpan.FromSeconds(30);
    private const int CombatDrainBatchSize = 500;
    private const int CombatDrainMaxRoundsPerCycle = 10;
    private const int CombatStatsEveryNthCycle = 10;

    // How often the RustArchon plugin's tool cupboard index is read while Recording is on. A base changes rarely, and
    // each read replaces the last picture wholesale, so once a minute is plenty and cheap.
    private static readonly TimeSpan TcPollInterval = TimeSpan.FromSeconds(60);
    private const int TcPageSize = 200;
    private const int TcMaxPagesPerRead = 50;

    // How often the plugin's recorded player positions are collected while Recording is on. Same reasoning as combat: the
    // plugin records into a bounded buffer regardless, so this only sets how fresh the Panel's view is.
    private static readonly TimeSpan PositionsDrainInterval = TimeSpan.FromSeconds(30);
    private const int PositionsDrainBatchSize = 500;
    private const int PositionsDrainMaxRoundsPerCycle = 10;

    // How often the plugin is asked about the world map. The picture changes once per wipe, so this is only how quickly a
    // fresh one is noticed (and an upload's outcome seen); the first read comes soon after the connection is up.
    private static readonly TimeSpan MapPollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MapPollInitialDelay = TimeSpan.FromSeconds(45);

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
        public decimal Framerate { get; set; }
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
    private readonly Task _pluginListPollLoopTask;
    private readonly Task _combatDrainLoopTask;
    private readonly Task _tcPollLoopTask;
    private readonly Task _positionsDrainLoopTask;
    private readonly Task _mapPollLoopTask;

    // Whether the plugin last reported the map capability. Fail closed.
    private volatile bool _mapPollEnabled;
    private bool _mapParseFailureLogged;

    // The world whose monuments were last sent to the Api ("size:seed"). They only change with a wipe, so once is enough.
    private string? _monumentsSentForWorld;

    // Whether the plugin last reported the positions capability AND the Recording switch on. Fail closed.
    private volatile bool _positionsDrainEnabled;
    private long _positionsBootId;
    private long _positionsCursor;
    private bool _positionsParseFailureLogged;

    // Whether the plugin last reported the tcs capability AND the Recording switch on. Fail closed: false until a
    // handshake says otherwise.
    private volatile bool _tcPollEnabled;
    private bool _tcParseFailureLogged;

    // Whether the plugin last reported the combat capability AND the Combat log switch on: the only time draining is
    // worth doing. Set by the handshake poll, read by the drain loop. Fail closed: false until a handshake says otherwise.
    private volatile bool _combatDrainEnabled;

    // Where the last drain left off. Actor-local and not persisted: after a Worker restart the drain starts from the
    // beginning of the plugin's buffer, and the Api drops anything it already stored (it keys on boot and sequence).
    private long _combatBootId;
    private long _combatCursor;
    private bool _combatParseFailureLogged;

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
            errorReconnectTimeout: reconnectOptions.ErrorReconnectTimeout,
            maxErrorReconnectTimeout: reconnectOptions.MaxErrorReconnectTimeout);
        _client.ConnectionChanged += OnConnectionChanged;
        _client.MessageReceived += OnRawMessageReceived;
        _client.ProcessingError += OnProcessingError;

        _heartbeatLoopTask = Task.Run(() => HeartbeatLoopAsync(_lifetimeCts.Token));
        _playerListPollLoopTask = Task.Run(() => PlayerListPollLoopAsync(_lifetimeCts.Token));
        _serverInfoPollLoopTask = Task.Run(() => ServerInfoPollLoopAsync(_lifetimeCts.Token));
        _pluginListPollLoopTask = Task.Run(() => PluginListPollLoopAsync(_lifetimeCts.Token));
        _combatDrainLoopTask = Task.Run(() => CombatDrainLoopAsync(_lifetimeCts.Token));
        _tcPollLoopTask = Task.Run(() => TcPollLoopAsync(_lifetimeCts.Token));
        _positionsDrainLoopTask = Task.Run(() => PositionsDrainLoopAsync(_lifetimeCts.Token));
        _mapPollLoopTask = Task.Run(() => MapPollLoopAsync(_lifetimeCts.Token));

        _ = PublishStatusAsync(RconConnectionStatus.Connecting, $"Connecting to {host}:{port}", CancellationToken.None);
        if (!_client.Connect(out var startException))
        {
            // Not fatal - Websocket.Client keeps retrying on its own regardless of whether the very
            // first attempt threw synchronously. See RustWebRconClient.Connect's remarks. Previously
            // only ever logged locally, same gap as Socket_OnClose's exception used to be - the status
            // published here was nothing at all, leaving the Panel showing "Connecting" indefinitely
            // with no indication anything had actually gone wrong. RconConnectionStatus.Error, not
            // Reconnecting: this is "never got a connection going in the first place", a genuinely
            // worse state than the normal was-connected-now-retrying case OnConnectionChanged reports.
            _logger.LogWarning(startException, "Initial connection attempt to server {ServerId} did not start cleanly; the client will keep retrying on its own", serverId);
            var detail = startException is null
                ? "Initial connection attempt did not start cleanly - retrying automatically"
                : $"Initial connection attempt did not start cleanly ({startException.Message}) - retrying automatically";
            _ = PublishStatusAsync(RconConnectionStatus.Error, detail, CancellationToken.None);
        }
    }

    public Guid ServerId { get; }

    /// <summary>
    /// Sends a command over the live connection and awaits its response.
    /// </summary>
    /// <param name="context">
    /// No default - every caller has to say whether this is a human-triggered command or a backend
    /// fetch reusing this same pathway, so it's a compile error to add a new caller without answering
    /// that. See <see cref="RconCommandContext"/>'s remarks and <see cref="OnRawMessageReceived"/>.
    /// </param>
    /// <returns>
    /// A result with <c>Error = "NotConnected"</c> if the socket isn't open right now, or
    /// <c>Error = "Timeout"</c> if no response arrives within <paramref name="timeout"/>.
    /// </returns>
    public async Task<RconCommandResult> SendCommandAsync(
        string command, TimeSpan? timeout, CancellationToken cancellationToken, RconCommandContext context)
    {
        // Captured here, not inside RustWebRconClient - this is the one place that knows both the
        // actual command text and whether it's interactive before anything is sent. There's no
        // response-correlated Identifier yet at this point (RustWebRconClient generates that internally
        // and doesn't hand it back synchronously), so a Sent frame publishes Identifier: 0 rather than
        // trying to thread it through; the Received frame that (usually) follows still carries its own
        // real Identifier. Fire-and-forget, same as every other publish in this class - a lost Sent
        // frame here isn't worth failing the command over.
        _ = _publishEndpoint.Publish(new RconFrameCaptured(
            ServerId, _tenantId, DateTimeOffset.UtcNow, Identifier: 0, Type: string.Empty, command,
            Stacktrace: null, context.Interactive, RconEventDirection.Sent));

        try
        {
            var response = await _client.SendCommandAsync(command, timeout ?? DefaultCommandTimeout, cancellationToken, context);
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

        // Fire-and-forget, same as the other synchronous event handlers' publishes in this class
        // (OnRawMessageReceived, HandleConnectionEvent) - this method itself can't be async since it's
        // wired directly to RustWebRconClient.ProcessingError. Error level, but no PublishStatusAsync -
        // the connection itself is fine (see this handler's own doc remarks), so this must not touch
        // the status pipeline that drives the live "connected" badge.
        _ = PublishDiagnosticAsync(ConnectionLogLevel.Error, $"Error processing an inbound frame: {e.Message}", CancellationToken.None);
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

        // Every response is captured and persisted, unconditionally - see RconFrameCaptured's remarks
        // for why this used to suppress non-interactive responses here and no longer does. What used to
        // be a publish/don't-publish decision at this layer is now just a data field: an unsolicited
        // frame (no RconCommandContext attached - e.UserData is null, e.g. an unprompted console line)
        // is always interactive, since RustArchon never triggers those itself; a command's response
        // carries through whatever RconCommandContext.Interactive its sender attached.
        var interactive = e.UserData is not RconCommandContext { Interactive: false };

        // The one thing not stored: a background drain poll's "nothing new since last time". On a quiet server that is nearly
        // every answer (thousands a day per server), and it carries no information the next real answer does not. Anything
        // else - data, an error, a "lost" or "reset" flag, or a person's own command - is stored as before.
        if (interactive || !EmptyDrainReply.IsEmpty(response.Message))
        {
            _ = _publishEndpoint.Publish(new RconFrameCaptured(
                ServerId,
                _tenantId,
                DateTimeOffset.UtcNow,
                response.Identifier,
                response.Type,
                response.Message,
                response.Stacktrace,
                interactive,
                RconEventDirection.Received));
        }

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
                // RconCommandContext.Background - this is RustArchon's own internal reconciliation
                // check, not something the user ran, so its response is persisted but hidden from an
                // ordinary user's Console tab (see RconCommandContext's remarks). A user-run
                // "playerlist" from the Console tab goes through the public SendCommandAsync wrapper
                // above with an Interactive: true context and does show up, same as any other command
                // they send. Called directly against _client, not through this class's own
                // SendCommandAsync wrapper above, so this loop's "Sent" side is deliberately never
                // published - a fixed command on a fixed schedule doesn't need its own Sent/Received
                // pairing in the console history the way an operator-typed command does; only the
                // response (already interesting for reconciliation) is captured.
                var response = await _client.SendCommandAsync(
                    "playerlist", DefaultCommandTimeout, cancellationToken, RconCommandContext.Background);
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
                await PublishDiagnosticAsync(ConnectionLogLevel.Warning, $"Player-list poll failed: {ex.Message}", cancellationToken);
                continue;
            }

            try
            {
                await ReconcilePlayerListAsync(currentPlayers, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reconcile player list for server {ServerId}", ServerId);
                await PublishDiagnosticAsync(ConnectionLogLevel.Warning, $"Failed to reconcile player list: {ex.Message}", cancellationToken);
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
                // RconCommandContext.Background - same reasoning as the playerlist poll above: this is
                // RustArchon's own internal telemetry capture, not something a user ran, so its
                // response is persisted but hidden from an ordinary user's console history, and its
                // Sent side is deliberately never published either.
                var response = await _client.SendCommandAsync(
                    "serverinfo", DefaultCommandTimeout, cancellationToken, RconCommandContext.Background);
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
                await PublishDiagnosticAsync(ConnectionLogLevel.Warning, $"serverinfo poll failed: {ex.Message}", cancellationToken);
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
                        info.Memory, info.Framerate, DateTimeOffset.UtcNow),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish serverinfo snapshot for server {ServerId}", ServerId);
            }
        }
    }

    /// <summary>
    /// Polls the server's plugin list on a fixed interval and publishes <see cref="ServerPluginsCaptured"/>
    /// with the complete current list - see that record's remarks.
    /// </summary>
    private async Task PluginListPollLoopAsync(CancellationToken cancellationToken)
    {
        var delay = PluginListInitialDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Pessimistic until this attempt actually gets an answer from the server.
            delay = PluginListRetryInterval;

            ServerModFramework framework;
            IReadOnlyList<ServerPluginInfo> plugins;
            try
            {
                (framework, plugins) = await QueryPluginListAsync(cancellationToken);
            }
            catch (InvalidOperationException)
            {
                continue; // Not connected right now - retried on the short interval.
            }
            catch (TimeoutException)
            {
                continue; // No response in time - treated the same as "not connected right now".
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin-list poll failed for server {ServerId}", ServerId);
                await PublishDiagnosticAsync(ConnectionLogLevel.Warning, $"Plugin-list poll failed: {ex.Message}", cancellationToken);
                continue;
            }

            delay = PluginListPollInterval;

            try
            {
                await _publishEndpoint.Publish(
                    new ServerPluginsCaptured(ServerId, _tenantId, framework, plugins, DateTimeOffset.UtcNow),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish plugin list for server {ServerId}", ServerId);
            }

            await PollPluginHandshakeAsync(plugins, cancellationToken);
        }
    }

    /// <summary>
    /// Asks Carbon first (<c>c.plugins</c>), then Oxide (<c>o.plugins</c>) only if that produced no
    /// plugins - Carbon ships an Oxide compatibility layer, so a Carbon server may well also answer
    /// <c>o.plugins</c>, whereas an Oxide server never answers <c>c.plugins</c>. Probing both every time
    /// (rather than trusting <see cref="RustWebRconClient.DetectedModFramework"/>) means a framework
    /// installed or removed after this actor started is noticed without a worker restart. Neither
    /// producing a plugin means <see cref="ServerModFramework.None"/> - which is also what a framework
    /// with zero plugins loaded looks like, and reads the same to a user: nothing to list.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="TimeoutException">No response within <see cref="DefaultCommandTimeout"/>.</exception>
    private async Task<(ServerModFramework Framework, IReadOnlyList<ServerPluginInfo> Plugins)> QueryPluginListAsync(
        CancellationToken cancellationToken)
    {
        // RconCommandContext.Background - same reasoning as the playerlist/serverinfo polls above.
        var carbon = await _client.SendCommandAsync(
            "c.plugins", DefaultCommandTimeout, cancellationToken, RconCommandContext.Background);
        var carbonPlugins = ServerPluginListParser.Parse(ServerModFramework.Carbon, carbon.Message);
        if (carbonPlugins.Count > 0)
        {
            return (ServerModFramework.Carbon, carbonPlugins);
        }

        var oxide = await _client.SendCommandAsync(
            "o.plugins", DefaultCommandTimeout, cancellationToken, RconCommandContext.Background);
        var oxidePlugins = ServerPluginListParser.Parse(ServerModFramework.Oxide, oxide.Message);
        return oxidePlugins.Count > 0
            ? (ServerModFramework.Oxide, oxidePlugins)
            : (ServerModFramework.None, []);
    }

    /// <summary>
    /// Says <c>archon.hello</c> to the optional RustArchon companion plugin and publishes what it answers as
    /// <see cref="ServerPluginHandshakeCaptured"/>. Runs on the plugin-list poll's schedule, and only when
    /// that list <em>positively</em> shows the plugin loaded - a server that does not list it is never sent
    /// the command, so it never sees an "Unknown command" for a plugin it does not have.
    /// </summary>
    /// <remarks>
    /// Settings drift (a plugin reinstall, a reset settings file) is corrected by the Api when this message
    /// arrives; a Panel toggle is pushed to the plugin immediately by the Api and does not wait for this poll.
    /// </remarks>
    private async Task PollPluginHandshakeAsync(IReadOnlyList<ServerPluginInfo> plugins, CancellationToken cancellationToken)
    {
        if (!ArchonHelloParser.IsPluginListed(plugins))
        {
            _combatDrainEnabled = false;
            _tcPollEnabled = false;
            _positionsDrainEnabled = false;
            _mapPollEnabled = false;
            return;
        }

        try
        {
            // Background: this is our own bookkeeping poll, not something a person typed - same reasoning as
            // the plugin-list/playerlist/serverinfo polls.
            var reply = await _client.SendCommandAsync(
                "archon.hello", DefaultCommandTimeout, cancellationToken, RconCommandContext.Background);

            if (!ArchonHelloParser.TryParse(reply.Message, out var hello, out var failure) || hello is null)
            {
                // The plugin is listed but did not give an answer we understand (an old build, a broken one).
                // Not publishing means every plugin-only feature stays off, which is the safe direction.
                _combatDrainEnabled = false;
                _tcPollEnabled = false;
                _positionsDrainEnabled = false;
                _mapPollEnabled = false;
                await PublishDiagnosticAsync(
                    ConnectionLogLevel.Warning,
                    $"The RustArchon plugin is loaded but its archon.hello reply was not understood ({failure}); plugin features stay off.",
                    cancellationToken);
                return;
            }

            _combatDrainEnabled = hello.CombatLogEnabled
                && hello.Capabilities.Contains(RustArchonPlugin.CombatCapability, StringComparer.Ordinal);
            _tcPollEnabled = hello.RecordingEnabled
                && hello.Capabilities.Contains(RustArchonPlugin.TcsCapability, StringComparer.Ordinal);
            _positionsDrainEnabled = hello.RecordingEnabled
                && hello.Capabilities.Contains(RustArchonPlugin.PositionsCapability, StringComparer.Ordinal);
            _mapPollEnabled = hello.Capabilities.Contains(RustArchonPlugin.MapCapability, StringComparer.Ordinal);

            await _publishEndpoint.Publish(
                new ServerPluginHandshakeCaptured(
                    ServerId,
                    _tenantId,
                    hello.ProtocolVersion,
                    hello.PluginVersion,
                    hello.Capabilities,
                    hello.RecordingEnabled,
                    hello.CombatLogEnabled,
                    hello.SettingsPersisted,
                    DateTimeOffset.UtcNow,
                    hello.SigningState,
                    hello.SigningKeyFingerprint),
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Not connected right now - the next poll tries again.
        }
        catch (TimeoutException)
        {
            // No reply in time - treated the same as not connected.
        }
        catch (OperationCanceledException)
        {
            // Shutting down - the loop's next delay ends it.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RustArchon plugin handshake failed for server {ServerId}", ServerId);
        }
    }

    /// <summary>
    /// Collects the plugin's combat events every <see cref="CombatDrainInterval"/> while it reports the capability and
    /// the switch on, and hands them to the Api. Silent when off: nothing is asked of the plugin.
    /// </summary>
    private async Task CombatDrainLoopAsync(CancellationToken cancellationToken)
    {
        var cycles = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CombatDrainInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!_combatDrainEnabled)
            {
                continue;
            }

            await DrainCombatEventsAsync(cancellationToken);

            if (++cycles % CombatStatsEveryNthCycle == 0)
            {
                await LogPluginStatsAsync(cancellationToken);
            }
        }
    }

    /// <summary>Asks the plugin about the world map every <see cref="MapPollInterval"/> while it reports the capability.</summary>
    private async Task MapPollLoopAsync(CancellationToken cancellationToken)
    {
        var delay = MapPollInitialDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = MapPollInterval;
            if (_mapPollEnabled)
            {
                await ReadMapStatusAsync(cancellationToken);
            }
        }
    }

    // Reads the plugin's map status and publishes it. The first time a world is seen the monument list is fetched and sent
    // along (a failure to get it does not stop the status: it is asked for again next time).
    private async Task ReadMapStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reply = await _client.SendCommandAsync(
                "archon.map.status", DefaultCommandTimeout, cancellationToken, RconCommandContext.Background);

            if (!ArchonMapParser.TryParseStatus(reply.Message, out var status, out var failure) || status is null)
            {
                if (!_mapParseFailureLogged)
                {
                    _mapParseFailureLogged = true;
                    _logger.LogWarning(
                        "Server {ServerId}: the RustArchon plugin's map status reply was not understood ({Failure}); nothing was stored.",
                        ServerId, failure);
                }

                return;
            }

            _mapParseFailureLogged = false;

            // Nothing to report until the game has loaded a world.
            if (!status.WorldKnown)
            {
                return;
            }

            string? monumentsJson = null;
            var world = $"{status.WorldSize}:{status.WorldSeed}";
            if (world != _monumentsSentForWorld)
            {
                monumentsJson = await ReadMonumentsAsync(cancellationToken);
            }

            await _publishEndpoint.Publish(
                new PluginMapStatusCaptured(
                    ServerId, _tenantId, status.WorldSize, status.WorldSeed, status.FileName, status.Exists, status.Bytes,
                    status.UploadState, monumentsJson, DateTimeOffset.UtcNow),
                cancellationToken);

            if (monumentsJson is not null)
            {
                _monumentsSentForWorld = world;
            }
        }
        catch (InvalidOperationException)
        {
            // Not connected right now; the next cycle tries again.
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Map status read failed for server {ServerId}", ServerId);
        }
    }

    private async Task<string?> ReadMonumentsAsync(CancellationToken cancellationToken)
    {
        var reply = await _client.SendCommandAsync(
            "archon.map.monuments", DefaultCommandTimeout, cancellationToken, RconCommandContext.Background);

        if (ArchonMapParser.TryParseMonuments(reply.Message, out var monuments, out var failure))
        {
            return monuments;
        }

        _logger.LogWarning("Server {ServerId}: the RustArchon plugin's monument list was not understood ({Failure}).", ServerId, failure);
        return null;
    }

    /// <summary>Collects the plugin's recorded positions every <see cref="PositionsDrainInterval"/> while it reports the capability and Recording is on.</summary>
    private async Task PositionsDrainLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PositionsDrainInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_positionsDrainEnabled)
            {
                await DrainPositionsAsync(cancellationToken);
            }
        }
    }

    /// <summary>Reads the plugin's tool cupboard index every <see cref="TcPollInterval"/> while it reports the capability and Recording is on.</summary>
    private async Task TcPollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TcPollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_tcPollEnabled)
            {
                await ReadTcIndexAsync(cancellationToken);
            }
        }
    }

    // Walks the plugin's pages (each names where the next starts) and publishes the whole list as one snapshot. Any page that
    // fails or is not understood abandons the read: a snapshot missing a page would look like bases that vanished.
    private async Task ReadTcIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            var all = new List<string>();
            var ready = false;
            var offset = 0;

            for (var pageNumber = 0; pageNumber < TcMaxPagesPerRead; pageNumber++)
            {
                var reply = await _client.SendCommandAsync(
                    $"archon.tcs {offset} {TcPageSize}", DefaultCommandTimeout, cancellationToken, RconCommandContext.Background);

                if (!ArchonTcsParser.TryParse(reply.Message, out var page, out var failure) || page is null)
                {
                    if (!_tcParseFailureLogged)
                    {
                        _tcParseFailureLogged = true;
                        _logger.LogWarning(
                            "Server {ServerId}: the RustArchon plugin's tool cupboard reply was not understood ({Failure}); nothing was stored.",
                            ServerId, failure);
                    }

                    return;
                }

                _tcParseFailureLogged = false;
                all.AddRange(page.TcsRaw);
                ready = page.Ready;

                if (page.Next >= page.Total || page.Next <= offset)
                {
                    await _publishEndpoint.Publish(
                        new PluginTcSnapshotCaptured(
                            ServerId, _tenantId, ready, all.Count, "[" + string.Join(",", all) + "]", DateTimeOffset.UtcNow),
                        cancellationToken);
                    return;
                }

                offset = page.Next;
            }

            _logger.LogWarning("Server {ServerId}: the tool cupboard list did not finish within {Pages} pages; nothing was stored.", ServerId, TcMaxPagesPerRead);
        }
        catch (InvalidOperationException)
        {
            // Not connected right now; the next cycle tries again.
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool cupboard read failed for server {ServerId}", ServerId);
        }
    }

    // Drains until caught up, or a bounded number of rounds per cycle so one very busy server cannot monopolize the loop.
    private async Task DrainCombatEventsAsync(CancellationToken cancellationToken)
    {
        for (var round = 0; round < CombatDrainMaxRoundsPerCycle && !cancellationToken.IsCancellationRequested; round++)
        {
            try
            {
                var reply = await _client.SendCommandAsync(
                    $"archon.events.drain {_combatBootId} {_combatCursor} {CombatDrainBatchSize}",
                    DefaultCommandTimeout, cancellationToken, RconCommandContext.Background);

                if (!ArchonEventsParser.TryParse(reply.Message, out var drain, out var failure) || drain is null)
                {
                    // Once per actor: a plugin that answers in a way we do not understand would otherwise log every 30 s.
                    if (!_combatParseFailureLogged)
                    {
                        _combatParseFailureLogged = true;
                        _logger.LogWarning(
                            "Server {ServerId}: the RustArchon plugin's combat drain reply was not understood ({Failure}); nothing was stored.",
                            ServerId, failure);
                    }

                    return;
                }

                _combatParseFailureLogged = false;

                if (drain.Count > 0)
                {
                    // Publish BEFORE moving the cursor: if this throws the same batch is asked for again, and the Api
                    // drops what it already has, so nothing is lost and nothing is stored twice.
                    await _publishEndpoint.Publish(
                        new PluginCombatEventsCaptured(
                            ServerId, _tenantId, drain.BootId, drain.Reset, drain.Lost,
                            drain.FirstSequence, drain.LastSequence, drain.Count, drain.EventsJson, DateTimeOffset.UtcNow),
                        cancellationToken);
                }

                _combatBootId = drain.BootId;
                _combatCursor = drain.Cursor;

                if (drain.Cursor >= drain.Head)
                {
                    return; // caught up
                }
            }
            catch (InvalidOperationException)
            {
                return; // not connected right now; the next cycle tries again
            }
            catch (TimeoutException)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Combat event drain failed for server {ServerId}", ServerId);
                return;
            }
        }
    }

    // Same shape as the combat drain: publish BEFORE moving the cursor, bounded rounds per cycle, the Api drops repeats.
    private async Task DrainPositionsAsync(CancellationToken cancellationToken)
    {
        for (var round = 0; round < PositionsDrainMaxRoundsPerCycle && !cancellationToken.IsCancellationRequested; round++)
        {
            try
            {
                var reply = await _client.SendCommandAsync(
                    $"archon.positions.drain {_positionsBootId} {_positionsCursor} {PositionsDrainBatchSize}",
                    DefaultCommandTimeout, cancellationToken, RconCommandContext.Background);

                if (!ArchonPositionsParser.TryParse(reply.Message, out var drain, out var failure) || drain is null)
                {
                    if (!_positionsParseFailureLogged)
                    {
                        _positionsParseFailureLogged = true;
                        _logger.LogWarning(
                            "Server {ServerId}: the RustArchon plugin's positions drain reply was not understood ({Failure}); nothing was stored.",
                            ServerId, failure);
                    }

                    return;
                }

                _positionsParseFailureLogged = false;

                if (drain.Count > 0)
                {
                    await _publishEndpoint.Publish(
                        new PluginPositionsCaptured(
                            ServerId, _tenantId, drain.BootId, drain.Reset, drain.Lost,
                            drain.FirstSequence, drain.LastSequence, drain.Count, drain.EventsJson, DateTimeOffset.UtcNow),
                        cancellationToken);
                }

                _positionsBootId = drain.BootId;
                _positionsCursor = drain.Cursor;

                if (drain.Cursor >= drain.Head)
                {
                    return; // caught up
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }
            catch (TimeoutException)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Position drain failed for server {ServerId}", ServerId);
                return;
            }
        }
    }

    // The plugin's own cost counters (hook fires, recorded, buffered), logged now and then for measuring the hooks.
    private async Task LogPluginStatsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reply = await _client.SendCommandAsync(
                "archon.stats", DefaultCommandTimeout, cancellationToken, RconCommandContext.Background);
            _logger.LogInformation("Server {ServerId}: RustArchon plugin stats {Stats}", ServerId, reply.Message);
        }
        catch (Exception)
        {
            // Purely informational; never let it disturb the drain.
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

    /// <summary>
    /// Appends a Logs-tab entry for something worth surfacing that isn't itself a connection-status
    /// transition - a parse error, a poll failure, ... - see <see cref="WorkerDiagnosticLogged"/>'s
    /// remarks. Deliberately only used for failures talking to (or parsing a response from) the actual
    /// Rust server - a failure publishing *to the message bus itself* (heartbeat, snapshot publish, ...)
    /// is a different failure domain this method's own publish would likely also be hitting, so those
    /// stay local-only (<c>_logger.LogWarning</c>) rather than risking a publish call reporting on its
    /// own kind of failure.
    /// </summary>
    private async Task PublishDiagnosticAsync(ConnectionLogLevel level, string message, CancellationToken cancellationToken)
    {
        try
        {
            await _publishEndpoint.Publish(
                new WorkerDiagnosticLogged(ServerId, _tenantId, level, message, DateTimeOffset.UtcNow),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish diagnostic log entry for server {ServerId}", ServerId);
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

        try
        {
            await _pluginListPollLoopTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        try
        {
            await _combatDrainLoopTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        try
        {
            await _tcPollLoopTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        try
        {
            await _positionsDrainLoopTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        try
        {
            await _mapPollLoopTask;
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
