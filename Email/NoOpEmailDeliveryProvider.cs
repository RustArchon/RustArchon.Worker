using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RustArchon.Worker.Email;

/// <summary>
/// A no-operation email delivery provider that does nothing.
/// This is used as a fallback when no proper email provider is configured.
/// </summary>
public class NoOpEmailDeliveryProvider : IEmailDeliveryProvider
{
    private readonly ILogger<NoOpEmailDeliveryProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NoOpEmailDeliveryProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public NoOpEmailDeliveryProvider(ILogger<NoOpEmailDeliveryProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<bool> SendEmailAsync(EmailMessage message)
    {
        _logger.LogWarning("Email sending called but no email provider configured. Email would be sent to {To}", message?.To ?? "unknown recipient");
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> VerifyConfigurationAsync()
    {
        _logger.LogWarning("Email configuration verification called but no email provider is configured.");
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public string ProviderName => "None (No Operation)";
}