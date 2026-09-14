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
    /// <remarks>
    /// The full body is logged deliberately, not just the recipient - this is the only way to reach a
    /// confirmation/reset link on a deployment that hasn't configured a real provider yet (see
    /// DEPLOYMENT.md's "First login" step, which greps this exact log line). A wording change here
    /// broke that grep once already - keep the "Would send email" phrase and the body together if this
    /// is ever touched again.
    /// </remarks>
    public Task<bool> SendEmailAsync(EmailMessage message)
    {
        _logger.LogWarning(
            "No email provider configured. Would send email to {To} with subject {Subject}:\n{Body}",
            message?.To ?? "unknown recipient", message?.Subject ?? string.Empty, message?.Body ?? string.Empty);
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