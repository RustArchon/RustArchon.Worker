// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RustArchon.Worker.Email;

/// <summary>
/// Stands in for whatever provider <see cref="Security.InternalEmailSettings"/> actually names, when
/// this process was started with <c>RUSTARCHON_SUPPRESS_EMAIL_DELIVERY=true</c> - see
/// <see cref="EmailDeliveryOptions"/>'s remarks for why this exists and
/// <see cref="EmailDeliveryProviderFactory"/> for how it takes precedence.
/// </summary>
/// <remarks>
/// Deliberately a distinct type from <see cref="NoOpEmailDeliveryProvider"/>, even though both send
/// nothing: <c>NoOp</c> means "nobody configured a provider," while this means "a provider is
/// configured and would otherwise be used, but this deployment has been told not to actually send" -
/// worth telling apart in these logs and in <c>ProviderName</c> when troubleshooting why a test
/// deployment's email settings seem to do nothing.
/// </remarks>
public class SuppressedEmailDeliveryProvider : IEmailDeliveryProvider
{
    private readonly ILogger<SuppressedEmailDeliveryProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SuppressedEmailDeliveryProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public SuppressedEmailDeliveryProvider(ILogger<SuppressedEmailDeliveryProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<bool> SendEmailAsync(EmailMessage message)
    {
        // Reports success, same reasoning as NoOpEmailDeliveryProvider: this is a deliberate,
        // deployment-level choice, not a failure - EmailRequestedConsumer's retry policy shouldn't
        // keep hammering a send that was never going to happen either way.
        _logger.LogWarning(
            "Email to {To} suppressed by RUSTARCHON_SUPPRESS_EMAIL_DELIVERY - not actually sent: {Subject}",
            message?.To ?? "unknown recipient", message?.Subject ?? "(no subject)");
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> VerifyConfigurationAsync()
    {
        _logger.LogWarning("Email configuration verification suppressed by RUSTARCHON_SUPPRESS_EMAIL_DELIVERY.");
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public string ProviderName => "Suppressed (dev mode)";
}
