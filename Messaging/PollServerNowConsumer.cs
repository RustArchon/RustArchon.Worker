// Copyright ©2026 Scott Blomfield

using MassTransit;
using RustArchon.Messaging.Contracts;
using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Messaging;

/// <summary>
/// Consumes <see cref="PollServerNow"/> - a fanout message every worker instance receives its own copy of, exactly like
/// <see cref="SendRconCommandConsumer"/>. Only the instance holding the connection actor for the target server responds; every other instance
/// consumes it and does nothing, so the request client's one real response always comes from the true owner.
/// </summary>
public class PollServerNowConsumer(IConnectionSupervisor supervisor) : IConsumer<PollServerNow>
{
    public async Task Consume(ConsumeContext<PollServerNow> context)
    {
        if (!supervisor.TryGetActor(context.Message.ServerId, out var actor))
        {
            // Not ours - deliberately do not respond, letting the true owner's response (if any) resolve the request client's pending call.
            return;
        }

        var connected = await actor.PollNowAsync(context.Message.Polls.ToHashSet(StringComparer.Ordinal), context.CancellationToken);
        await context.RespondAsync(new PollServerNowResult(connected));
    }
}
