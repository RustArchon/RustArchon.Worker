// Copyright ©2026 Scott Blomfield

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RustArchon.Worker.Ticketing;

/// <summary>
/// POSTs a signed JSON payload to a site-owner-configured URL on every ticket event. Push-only and
/// one-way - see <c>PlatformSettingsRegistry.TicketingWebhookUrl</c>'s remarks for why this can never
/// be more than a notification feed, not a two-way sync with whatever system receives it.
/// </summary>
/// <remarks>
/// Deliberately lets a failed delivery (a non-success status, or the request throwing outright)
/// propagate rather than catching it and returning a bool the way an
/// <c>Email.IEmailDeliveryProvider</c> does - there's no <c>Communication</c>-style audit row here to
/// mark Sent/Bounced, so the only thing worth doing with a failure is exactly what letting the
/// exception reach <c>TicketEventConsumer</c> already gets for free: the receive endpoint's retry
/// policy (see <c>Program.cs</c>) retries with backoff, and if that's exhausted, MassTransit routes the
/// message to its error queue instead of silently losing the event.
/// </remarks>
public class WebhookTicketingIntegrationProvider(
    ILogger<WebhookTicketingIntegrationProvider> logger, HttpClient httpClient,
    string webhookUrl, string webhookSecret) : ITicketingIntegrationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task NotifyTicketCreatedAsync(Guid ticketId) =>
        SendAsync(new WebhookPayload("ticket.created", ticketId, null));

    public Task NotifyTicketMessageAddedAsync(Guid ticketId, Guid messageId) =>
        SendAsync(new WebhookPayload("ticket.message_added", ticketId, messageId));

    private async Task SendAsync(WebhookPayload payload)
    {
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        var signature = Sign(body, webhookSecret);

        using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Add("X-RustArchon-Signature", $"sha256={signature}");

        using var response = await httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Ticketing webhook to {webhookUrl} returned {(int)response.StatusCode}: {responseBody}");
        }

        logger.LogInformation(
            "Delivered {Event} for ticket {TicketId} to the configured webhook.", payload.Event, payload.TicketId);
    }

    /// <summary>Hex-encoded HMAC-SHA256 of <paramref name="body"/>, keyed by <paramref name="secret"/> -
    /// what the receiving system checks the <c>X-RustArchon-Signature</c> header against before
    /// trusting the payload, the same idea as Stripe's own webhook signing.</summary>
    private static string Sign(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexStringLower(hash);
    }

    private record WebhookPayload(string Event, Guid TicketId, Guid? MessageId);
}
