using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RustArchon.Worker.Email;

/// <summary>
/// An email delivery provider that uses Resend's HTTP API directly - no dependency on a Resend SDK
/// package, since sending an email through it is one small JSON POST.
/// </summary>
public class ResendEmailDeliveryProvider : IEmailDeliveryProvider
{
    private const string BaseUrl = "https://api.resend.com";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<ResendEmailDeliveryProvider> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public ResendEmailDeliveryProvider(
        ILogger<ResendEmailDeliveryProvider> logger, HttpClient httpClient, string apiKey)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    /// <inheritdoc />
    public async Task<bool> SendEmailAsync(EmailMessage message)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/emails")
            {
                Content = JsonContent.Create(
                    new ResendSendRequest(
                        message.From,
                        [message.To],
                        message.Subject,
                        message.IsHtml ? message.Body : null,
                        message.IsHtml ? null : message.Body,
                        Cc: [.. message.Cc],
                        Bcc: [.. message.Bcc]),
                    options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Email sent successfully to {To} via Resend", message.To);
                return true;
            }

            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Resend rejected the send to {To}: {StatusCode} {Body}", message.To, response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To} using Resend", message.To);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> VerifyConfigurationAsync()
    {
        // Resend has no dedicated "check my credentials" endpoint - listing domains is the lightest
        // authenticated call that exists, and answering at all (regardless of how many domains come
        // back) confirms the API key itself is valid.
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/domains");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify Resend configuration");
            return false;
        }
    }

    /// <inheritdoc />
    public string ProviderName => "Resend";

    private record ResendSendRequest(
        string From,
        string[] To,
        string Subject,
        [property: JsonPropertyName("html")] string? Html,
        [property: JsonPropertyName("text")] string? Text,
        string[] Cc,
        string[] Bcc);
}
