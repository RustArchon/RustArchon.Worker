// Copyright ©2026 Scott Blomfield

using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Messaging.Contracts;
using RustArchon.Worker.Email;

namespace RustArchon.Worker.Messaging;

/// <summary>
/// Consumes <see cref="EmailRequested"/> - a competing-consumer message, like
/// <see cref="ConnectToServerConsumer"/>: exactly one live worker instance should send each email, not
/// all of them.
/// </summary>
/// <remarks>
/// Owns none of the actual sending logic - that's <see cref="IEmailDeliveryProvider"/>'s job. This
/// class only decides what "success" and "failure" mean for MassTransit's purposes: returning
/// normally acks the message (RabbitMQ deletes it - see <see cref="EmailRequested"/>'s remarks on why
/// no separate "report success" round trip is needed for that), while letting an exception propagate
/// triggers the receive endpoint's retry policy (see <c>Program.cs</c>) and, if retries are exhausted,
/// MassTransit's default error queue - the message is never silently lost either way.
/// </remarks>
public class EmailRequestedConsumer(
    IEmailDeliveryProvider deliveryProvider,
    ILogger<EmailRequestedConsumer> logger) : IConsumer<EmailRequested>
{
    public async Task Consume(ConsumeContext<EmailRequested> context)
    {
        var message = context.Message;

        logger.LogInformation(
            "Sending email {MessageId} to {To}: {Subject}",
            message.MessageId, message.To, message.Subject);

        await deliveryProvider.SendAsync(message.To, message.Subject, message.HtmlBody, context.CancellationToken);
    }
}
