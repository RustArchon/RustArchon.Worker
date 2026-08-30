// Copyright ©2026 Scott Blomfield

using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Messaging.Contracts;
using RustArchon.Worker.Connections;
using RustArchon.Worker.Security;

namespace RustArchon.Worker.Messaging;

/// <summary>
/// Consumes <see cref="ServerLifecycleChanged"/> - a fanout message every worker instance receives
/// its own copy of. Self-filtered: an instance with no connection actor for the message's
/// <see cref="ServerLifecycleChanged.ServerId"/> has nothing to do.
/// </summary>
public class ServerLifecycleConsumer(
    IConnectionSupervisor supervisor,
    IInternalApiClient internalApiClient,
    ILogger<ServerLifecycleConsumer> logger) : IConsumer<ServerLifecycleChanged>
{
    public async Task Consume(ConsumeContext<ServerLifecycleChanged> context)
    {
        var message = context.Message;

        if (!supervisor.TryGetActor(message.ServerId, out _))
        {
            // Not ours - no-op.
            return;
        }

        switch (message.ChangeType)
        {
            case ServerLifecycleChangeType.Updated:
                var server = await internalApiClient.GetServerAsync(message.ServerId, context.CancellationToken);
                if (server is null)
                {
                    logger.LogWarning(
                        "Server {ServerId} reported as Updated but is no longer reachable via the internal API - stopping",
                        message.ServerId);
                    await supervisor.StopAsync(message.ServerId);
                    return;
                }

                await supervisor.StartOrRestartAsync(
                    message.ServerId,
                    message.TenantId,
                    server.Host,
                    server.Port,
                    server.RconPassword,
                    context.CancellationToken);
                break;

            case ServerLifecycleChangeType.Disabled:
            case ServerLifecycleChangeType.Deleted:
                await supervisor.StopAsync(message.ServerId);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(message), message.ChangeType, "Unrecognized server lifecycle change type.");
        }
    }
}
