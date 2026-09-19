// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Messaging.Contracts;
using RustArchon.Worker.Security;
using RustArchon.Worker.Ticketing;

namespace RustArchon.Worker.Messaging;

/// <summary>
/// Consumes <see cref="TicketCreated"/>/<see cref="TicketMessageAdded"/> - competing-consumer, like
/// <see cref="EmailRequestedConsumer"/>: exactly one live worker instance should mirror each event out,
/// not all of them.
/// </summary>
/// <remarks>
/// Owns none of the actual delivery logic, same split as <see cref="EmailRequestedConsumer"/>:
/// <see cref="ITicketingIntegrationProviderFactory"/> picks the provider from the platform's current
/// settings (fetched fresh on every event, never cached - see
/// <see cref="IInternalApiClient.GetTicketingSettingsAsync"/>'s remarks) and
/// <see cref="ITicketingIntegrationProvider"/> does the actual notify. A failure to resolve settings or
/// a provider throwing (see <see cref="WebhookTicketingIntegrationProvider"/>'s own remarks on why it
/// throws rather than swallowing a failed delivery) is left to propagate and retry - see this receive
/// endpoint's retry policy in <c>Program.cs</c>.
/// </remarks>
public class TicketEventConsumer(
    IInternalApiClient internalApiClient,
    ITicketingIntegrationProviderFactory providerFactory,
    ILogger<TicketEventConsumer> logger)
    : IConsumer<TicketCreated>, IConsumer<TicketMessageAdded>
{
    public async Task Consume(ConsumeContext<TicketCreated> context)
    {
        var message = context.Message;
        logger.LogInformation("Mirroring ticket {TicketId} created.", message.TicketId);

        var settings = await internalApiClient.GetTicketingSettingsAsync(context.CancellationToken);
        var provider = providerFactory.Resolve(settings);

        await provider.NotifyTicketCreatedAsync(message.TicketId);
    }

    public async Task Consume(ConsumeContext<TicketMessageAdded> context)
    {
        var message = context.Message;
        logger.LogInformation(
            "Mirroring message {MessageId} added to ticket {TicketId}.", message.MessageId, message.TicketId);

        var settings = await internalApiClient.GetTicketingSettingsAsync(context.CancellationToken);
        var provider = providerFactory.Resolve(settings);

        await provider.NotifyTicketMessageAddedAsync(message.TicketId, message.MessageId);
    }
}
