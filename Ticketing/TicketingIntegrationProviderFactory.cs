// Copyright ©2026 Scott Blomfield

using System.Net.Http;
using Microsoft.Extensions.Logging;
using RustArchon.Worker.Security;

namespace RustArchon.Worker.Ticketing;

/// <inheritdoc cref="ITicketingIntegrationProviderFactory" />
public class TicketingIntegrationProviderFactory(
    ILogger<WebhookTicketingIntegrationProvider> webhookLogger,
    ILogger<InternalNoOpTicketingIntegrationProvider> noOpLogger,
    IHttpClientFactory httpClientFactory) : ITicketingIntegrationProviderFactory
{
    /// <inheritdoc />
    public ITicketingIntegrationProvider Resolve(InternalTicketingSettings settings)
    {
        if (settings.Provider == TicketingProviders.Webhook
            && !string.IsNullOrEmpty(settings.WebhookUrl)
            && !string.IsNullOrEmpty(settings.WebhookSecret))
        {
            return new WebhookTicketingIntegrationProvider(
                webhookLogger, httpClientFactory.CreateClient(), settings.WebhookUrl, settings.WebhookSecret);
        }

        return new InternalNoOpTicketingIntegrationProvider(noOpLogger);
    }
}
