// Copyright ©2026 Scott Blomfield

using System.Collections.Concurrent;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RustArchon.Worker.Configuration;

namespace RustArchon.Worker.Connections;

/// <summary>
/// <see cref="IConnectionSupervisor"/> implementation. Registered as a singleton - one instance for
/// the whole worker process, holding every connection actor this instance currently owns.
/// </summary>
/// <remarks>
/// Takes <see cref="IBus"/>, not <see cref="IPublishEndpoint"/> - MassTransit registers
/// <c>IPublishEndpoint</c> as scoped, which a singleton can't safely consume directly (DI's
/// container-validation throws "Cannot consume scoped service ... from singleton ..." on startup if
/// it tries). <c>IBus</c> is registered as a singleton and itself implements
/// <see cref="IPublishEndpoint"/>, so it satisfies <see cref="ServerConnectionActor"/>'s constructor
/// parameter (still typed as <c>IPublishEndpoint</c>) with no further change needed there.
/// </remarks>
public class ConnectionSupervisor(
    IBus bus,
    WorkerIdentity workerIdentity,
    IOptions<ReconnectOptions> reconnectOptions,
    ILoggerFactory loggerFactory,
    ILogger<ConnectionSupervisor> logger) : IConnectionSupervisor
{
    private readonly ConcurrentDictionary<Guid, ServerConnectionActor> _actors = new();

    // Guards the whole check-remove-create-register sequence in StartOrRestartAsync below. Without
    // this, two ConnectToServer messages for the same server landing close together (the periodic
    // ServerClaimSweepService sweep racing a redelivered/duplicate message, say) can both pass
    // ConnectToServerConsumer's own TryGetActor pre-check before either has registered an actor here -
    // that check-then-act gap spans a real network round trip (GetServerAsync), not a tight window.
    // Both would then reach here, each construct its own ServerConnectionActor (each opening a real
    // RCON socket) and each write to _actors[serverId] - the loser's assignment is silently
    // overwritten, but its actor and socket are never disposed, leaking a second, untracked connection
    // to the same server that nothing ever stops or refreshes again. Confirmed live: this is exactly
    // what "Started connection"/"Connected to server" logging twice for one server id, back to back,
    // looks like - not a log duplication artifact. One process-wide semaphore is enough (this only
    // fires on a claim or a credential refresh, never a hot path), and it makes a losing racer just a
    // harmless redundant reconnect instead of an orphaned socket - the second call through still sees
    // the first's already-registered actor in _actors and tears it down cleanly before replacing it.
    private readonly SemaphoreSlim _startLock = new(1, 1);

    /// <inheritdoc />
    public bool TryGetActor(Guid serverId, out ServerConnectionActor actor) => _actors.TryGetValue(serverId, out actor!);

    /// <inheritdoc />
    public async Task StartOrRestartAsync(Guid serverId, Guid tenantId, string host, int port, string rconPassword, CancellationToken cancellationToken)
    {
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_actors.TryRemove(serverId, out var existing))
            {
                logger.LogInformation("Replacing existing connection for server {ServerId} with refreshed connection details", serverId);
                await existing.DisposeAsync();
            }

            var actor = new ServerConnectionActor(
                serverId,
                tenantId,
                host,
                port,
                rconPassword,
                workerIdentity.Id,
                bus,
                reconnectOptions.Value,
                loggerFactory.CreateLogger<ServerConnectionActor>());

            _actors[serverId] = actor;
            logger.LogInformation("Started connection for server {ServerId}", serverId);
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(Guid serverId)
    {
        if (_actors.TryRemove(serverId, out var actor))
        {
            await actor.DisposeAsync();
            logger.LogInformation("Stopped connection for server {ServerId}", serverId);
        }
    }
}
