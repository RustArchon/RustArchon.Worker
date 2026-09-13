// Copyright ©2026 Scott Blomfield

using System.Net.Http;
using Microsoft.Extensions.Logging;
using RustArchon.Worker.Security;

namespace RustArchon.Worker.Email;

/// <summary>
/// The values <see cref="InternalEmailSettings.ServiceProvider"/> can hold - mirrors
/// <c>RustArchon.Api.Infrastructure.PlatformSettingsRegistry.EmailProviders</c> exactly (same strings,
/// since this is the value the admin's <c>Choice</c> setting is saved as). Kept as a separate,
/// duplicated type here rather than a shared reference, the same way <c>InternalEmailSettings</c>
/// itself mirrors the Api's own DTO instead of referencing it - this Worker must never take a project
/// reference on RustArchon.Api directly (see <c>RustArchon.Panel.csproj</c>'s identical remark on why
/// not: it would pull the Api's entire dependency graph - EF Core, Npgsql, ASP.NET Core hosting - into
/// a process that only ever needs to talk to it over HTTP and RabbitMQ).
/// </summary>
public static class EmailProviders
{
    public const string Smtp = "Smtp";
    public const string Resend = "Resend";
    public const string SendGrid = "SendGrid";
}

/// <inheritdoc cref="IEmailDeliveryProviderFactory" />
/// <remarks>
/// Switches explicitly on <see cref="InternalEmailSettings.ServiceProvider"/> - an earlier version of
/// this inferred the provider from which fields were filled in (a non-empty API key meant SendGrid,
/// since that was the only API-based provider that existed yet). That broke the moment a second one
/// (Resend) existed: "the API key is set" no longer says which API it's for.
/// </remarks>
public class EmailDeliveryProviderFactory(
    ILogger<SmtpEmailDeliveryProvider> smtpLogger,
    ILogger<SendGridEmailDeliveryProvider> sendGridLogger,
    ILogger<ResendEmailDeliveryProvider> resendLogger,
    ILogger<NoOpEmailDeliveryProvider> noOpLogger,
    ILogger<SuppressedEmailDeliveryProvider> suppressedLogger,
    IHttpClientFactory httpClientFactory,
    EmailDeliveryOptions deliveryOptions) : IEmailDeliveryProviderFactory
{
    /// <inheritdoc />
    public IEmailDeliveryProvider Resolve(InternalEmailSettings settings)
    {
        // Checked first and unconditionally - a deployment-level kill switch overrides whatever the
        // platform's own (admin-editable) settings say, not just another provider choice among them.
        // See EmailDeliveryOptions's remarks.
        if (deliveryOptions.SuppressDelivery)
        {
            return new SuppressedEmailDeliveryProvider(suppressedLogger);
        }

        if (settings.ServiceProvider == EmailProviders.Resend && !string.IsNullOrEmpty(settings.ApiKey))
        {
            return new ResendEmailDeliveryProvider(resendLogger, httpClientFactory.CreateClient(), settings.ApiKey);
        }

        if (settings.ServiceProvider == EmailProviders.SendGrid && !string.IsNullOrEmpty(settings.ApiKey))
        {
            return new SendGridEmailDeliveryProvider(sendGridLogger, settings.ApiKey);
        }

        if (settings.ServiceProvider == EmailProviders.Smtp && !string.IsNullOrEmpty(settings.SmtpHost))
        {
            return new SmtpEmailDeliveryProvider(smtpLogger, new SmtpSettings
            {
                Host = settings.SmtpHost,
                Port = settings.SmtpPort,
                EnableSsl = settings.SmtpEnableSsl,
                Username = settings.SmtpUsername,
                Password = settings.SmtpPassword
            });
        }

        return new NoOpEmailDeliveryProvider(noOpLogger);
    }
}
