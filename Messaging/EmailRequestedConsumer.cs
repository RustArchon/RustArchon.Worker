using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Messaging.Contracts;
using RustArchon.Worker.Email;
using RustArchon.Worker.Security;

namespace RustArchon.Worker.Messaging;

/// <summary>
/// Consumes <see cref="EmailRequested"/> - a competing-consumer message, like
/// <see cref="ConnectToServerConsumer"/>: exactly one live worker instance should send each email, not
/// all of them.
/// </summary>
/// <remarks>
/// <para>
/// Owns none of the actual sending logic - <see cref="IEmailDeliveryProviderFactory"/> picks the
/// provider and <see cref="IEmailDeliveryProvider"/> does the send. This class only fetches the
/// settings that decide both (fresh, not cached - see <see cref="IInternalApiClient.GetEmailSettingsAsync"/>'s
/// remarks) and decides what "success" and "failure" mean for MassTransit's purposes: returning
/// normally acks the message (RabbitMQ deletes it - see <see cref="EmailRequested"/>'s remarks on why
/// no separate "report success" round trip is needed for that), while letting an exception propagate
/// triggers the receive endpoint's retry policy (see <c>Program.cs</c>) and, if retries are exhausted,
/// MassTransit's default error queue - the message is never silently lost either way.
/// </para>
/// <para>
/// <see cref="CommunicationDelivered"/> is published only once <see cref="IEmailDeliveryProvider.SendEmailAsync"/>
/// has actually returned - every current provider catches its own exceptions and answers with a
/// plain <c>bool</c> rather than throwing (see <c>SmtpEmailDeliveryProvider</c>), so this always
/// reaches that point. A failure to even get this far - <c>GetEmailSettingsAsync</c> itself throwing,
/// say - is left to propagate and retry as before, deliberately without publishing anything: the
/// matching <c>Communication</c> row stays Queued through the retries, since nothing definite has
/// happened to report yet.
/// </para>
/// </remarks>
public class EmailRequestedConsumer(
    IInternalApiClient internalApiClient,
    IEmailDeliveryProviderFactory providerFactory,
    IPublishEndpoint publishEndpoint,
    ILogger<EmailRequestedConsumer> logger) : IConsumer<EmailRequested>
{
    public async Task Consume(ConsumeContext<EmailRequested> context)
    {
        var message = context.Message;

        logger.LogInformation(
            "Sending email {MessageId} to {To}: {Subject}",
            message.MessageId, message.To, message.Subject);

        var settings = await internalApiClient.GetEmailSettingsAsync(context.CancellationToken);
        var provider = providerFactory.Resolve(settings);

        var emailMessage = new EmailMessage
        {
            To = message.To,
            From = settings.DefaultFromAddress,
            Subject = message.Subject,
            Body = message.HtmlBody,
            IsHtml = true  // For now assume everything is HTML as that's the typical pattern
        };

        var success = await provider.SendEmailAsync(emailMessage);

        await publishEndpoint.Publish(
            new CommunicationDelivered(
                message.MessageId,
                success,
                success ? null : $"The {provider.ProviderName} provider reported failure - see this worker's logs."),
            context.CancellationToken);
    }
}