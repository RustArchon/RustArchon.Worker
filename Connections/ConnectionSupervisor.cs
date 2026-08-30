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

    /// <inheritdoc />
    public bool TryGetActor(Guid serverId, out ServerConnectionActor actor) => _actors.TryGetValue(serverId, out actor!);

    /// <inheritdoc />
    public async Task StartOrRestartAsync(Guid serverId, Guid tenantId, string host, int port, string rconPassword, CancellationToken cancellationToken)
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
