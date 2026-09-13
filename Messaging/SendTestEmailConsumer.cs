// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using RustArchon.Messaging.Contracts;
using RustArchon.Worker.Email;
using RustArchon.Worker.Security;

namespace RustArchon.Worker.Messaging;

/// <summary>
/// Consumes <see cref="SendTestEmail"/> - a competing-consumer request/response, like
/// <see cref="EmailRequestedConsumer"/>'s message but answered rather than fire-and-forget, so the
/// admin settings page gets a definite pass/fail instead of having to go looking in these logs.
/// </summary>
/// <remarks>
/// Unlike <see cref="EmailRequestedConsumer"/>, a delivery failure here is caught and turned into a
/// normal (if unsuccessful) response rather than left to propagate - this is an interactive admin
/// action with someone waiting on an answer, not a queued send RabbitMQ's retry policy should keep
/// trying on its own schedule.
/// </remarks>
public class SendTestEmailConsumer(
    IInternalApiClient internalApiClient,
    IEmailDeliveryProviderFactory providerFactory,
    ILogger<SendTestEmailConsumer> logger) : IConsumer<SendTestEmail>
{
    public async Task Consume(ConsumeContext<SendTestEmail> context)
    {
        var message = context.Message;
        var settings = await internalApiClient.GetEmailSettingsAsync(context.CancellationToken);
        var provider = providerFactory.Resolve(settings);

        // Unlike a real EmailRequested send, where NoOpEmailDeliveryProvider/SuppressedEmailDeliveryProvider
        // reporting success is the right way to avoid pointless retries of a send that was never going
        // to happen, a *test* send saying "delivered" via a provider that sends nothing would be
        // actively misleading - the entire point of this button is telling the admin whether real
        // delivery works.
        if (provider is NoOpEmailDeliveryProvider)
        {
            await context.RespondAsync(new SendTestEmailResult(
                false,
                $"No usable configuration for the selected provider ({settings.ServiceProvider}) - " +
                    "check the Email settings.",
                "None"));
            return;
        }

        if (provider is SuppressedEmailDeliveryProvider)
        {
            await context.RespondAsync(new SendTestEmailResult(
                false,
                "This worker has RUSTARCHON_SUPPRESS_EMAIL_DELIVERY set - real delivery is disabled " +
                    "for this deployment, so a test send can't confirm anything. Unset it on this " +
                    "instance to test real delivery.",
                provider.ProviderName));
            return;
        }

        logger.LogInformation(
            "Sending test email to {To} via {ProviderName}", message.To, provider.ProviderName);

        try
        {
            var success = await provider.SendEmailAsync(new EmailMessage
            {
                To = message.To,
                From = settings.DefaultFromAddress,
                Subject = message.Subject,
                Body = message.HtmlBody,
                IsHtml = true
            });

            await context.RespondAsync(success
                ? new SendTestEmailResult(true, null, provider.ProviderName)
                : new SendTestEmailResult(false, "The provider reported failure - check this worker's logs.", provider.ProviderName));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Test email to {To} failed", message.To);
            await context.RespondAsync(new SendTestEmailResult(false, ex.Message, provider.ProviderName));
        }
    }
}
