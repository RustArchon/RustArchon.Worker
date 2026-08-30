// Copyright ©2026 Scott Blomfield

using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Messaging.Contracts;
using RustArchon.Worker.Connections;
using RustArchon.Worker.Security;

namespace RustArchon.Worker.Messaging;

/// <summary>
/// Consumes <see cref="ConnectToServer"/> - a competing-consumer message, so exactly one live worker
/// instance receives each publish. See the plan's §2.
/// </summary>
public class ConnectToServerConsumer(
    IConnectionSupervisor supervisor,
    IInternalApiClient internalApiClient,
    WorkerIdentity workerIdentity,
    ILogger<ConnectToServerConsumer> logger) : IConsumer<ConnectToServer>
{
    // Should stay well below the API's ServerClaimSweepService staleness threshold (~45s) - this is
    // the guard against a redundant/racy reminder landing on a second instance while the original
    // owner is still fine.
    private static readonly TimeSpan FreshOwnershipWindow = TimeSpan.FromSeconds(30);

    public async Task Consume(ConsumeContext<ConnectToServer> context)
    {
        var message = context.Message;

        if (supervisor.TryGetActor(message.ServerId, out _))
        {
            // Already ours - a redundant reminder, harmless.
            return;
        }

        var server = await internalApiClient.GetServerAsync(message.ServerId, context.CancellationToken);
        if (server is null)
        {
            logger.LogDebug("Server {ServerId} no longer exists or is disabled, ignoring claim", message.ServerId);
            return;
        }

        if (server.AssignedWorkerId is { } assignedWorkerId
            && assignedWorkerId != workerIdentity.Id
            && server.LastHeartbeatUtc is { } lastHeartbeatUtc
            && DateTimeOffset.UtcNow - lastHeartbeatUtc < FreshOwnershipWindow)
        {
            logger.LogDebug(
                "Server {ServerId} already freshly owned by worker {WorkerId}, skipping",
                message.ServerId,
                assignedWorkerId);
            return;
        }

        await supervisor.StartOrRestartAsync(
            message.ServerId,
            message.TenantId,
            server.Host,
            server.Port,
            server.RconPassword,
            context.CancellationToken);
    }
}
