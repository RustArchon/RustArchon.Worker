using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RustArchon.Worker.Email;

/// <summary>
/// An email delivery provider that uses SMTP.
/// </summary>
public class SmtpEmailDeliveryProvider : IEmailDeliveryProvider
{
    private readonly ILogger<SmtpEmailDeliveryProvider> _logger;
    private readonly SmtpSettings _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmtpEmailDeliveryProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="settings">The SMTP settings.</param>
    public SmtpEmailDeliveryProvider(
        ILogger<SmtpEmailDeliveryProvider> logger,
        SmtpSettings settings)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <inheritdoc />
    public async Task<bool> SendEmailAsync(EmailMessage message)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        try
        {
            using var client = new SmtpClient(_settings.Host, _settings.Port)
            {
                EnableSsl = _settings.EnableSsl,
                Credentials = new NetworkCredential(_settings.Username, _settings.Password)
            };

            var mailMessage = new MailMessage
            {
                From = new MailAddress(message.From),
                Subject = message.Subject,
                Body = message.Body,
                IsBodyHtml = message.IsHtml
            };

            mailMessage.To.Add(message.To);

            foreach (var cc in message.Cc)
            {
                mailMessage.CC.Add(cc);
            }

            foreach (var bcc in message.Bcc)
            {
                mailMessage.Bcc.Add(bcc);
            }

            await client.SendMailAsync(mailMessage);
            _logger.LogInformation("Email sent successfully to {To}", message.To);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To}", message.To);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> VerifyConfigurationAsync()
    {
        try
        {
            using var client = new SmtpClient(_settings.Host, _settings.Port)
            {
                EnableSsl = _settings.EnableSsl,
                Credentials = new NetworkCredential(_settings.Username, _settings.Password)
            };

            await client.SendMailAsync(new MailMessage(
                _settings.Username,
                _settings.Username,
                "Test Connection",
                "This is a test message to verify SMTP configuration."));
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify SMTP configuration");
            return false;
        }
    }

    /// <inheritdoc />
    public string ProviderName => "SMTP";
}