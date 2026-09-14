// Copyright ©2026 Scott Blomfield

using MassTransit;
using RustArchon.Messaging.Contracts;
using RustArchon.Rcon;
using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Messaging;

/// <summary>
/// Consumes <see cref="SendRconCommand"/> - a fanout message every worker instance receives its own
/// copy of. Only the instance holding the connection actor for the target server responds; every
/// other instance consumes it and does nothing, so the request client's one real response always
/// comes from the true owner. See the plan's §5.
/// </summary>
public class SendRconCommandConsumer(IConnectionSupervisor supervisor) : IConsumer<SendRconCommand>
{
    public async Task Consume(ConsumeContext<SendRconCommand> context)
    {
        if (!supervisor.TryGetActor(context.Message.ServerId, out var actor))
        {
            // Not ours - deliberately do not respond, letting the true owner's response (if any)
            // resolve the request client's pending call.
            return;
        }

        var result = await actor.SendCommandAsync(
            context.Message.Command, timeout: null, context.CancellationToken,
            new RconCommandContext(context.Message.Interactive));
        await context.RespondAsync(result);
    }
}
