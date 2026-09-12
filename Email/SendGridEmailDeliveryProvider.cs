using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace RustArchon.Worker.Email;

/// <summary>
/// An email delivery provider that uses SendGrid.
/// </summary>
public class SendGridEmailDeliveryProvider : IEmailDeliveryProvider
{
    private readonly ILogger<SendGridEmailDeliveryProvider> _logger;
    private readonly string _apiKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="SendGridEmailDeliveryProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="apiKey">The SendGrid API key.</param>
    public SendGridEmailDeliveryProvider(
        ILogger<SendGridEmailDeliveryProvider> logger,
        string apiKey)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    /// <inheritdoc />
    public async Task<bool> SendEmailAsync(EmailMessage message)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));

        try
        {
            var client = new SendGridClient(_apiKey);
            
            var from = new EmailAddress(message.From);
            var to = new EmailAddress(message.To);
            var subject = message.Subject;
            var plainTextContent = message.Body;
            var htmlContent = message.IsHtml ? message.Body : null;

            var msg = MailHelper.CreateSingleEmail(from, to, subject, plainTextContent, htmlContent);

            foreach (var cc in message.Cc)
            {
                msg.AddCc(cc);
            }

            foreach (var bcc in message.Bcc)
            {
                msg.AddBcc(bcc);
            }

            var response = await client.SendEmailAsync(msg);
            
            _logger.LogInformation("Email sent successfully to {To} with SendGrid", message.To);
            return response.StatusCode == System.Net.HttpStatusCode.Accepted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To} using SendGrid", message.To);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> VerifyConfigurationAsync()
    {
        try
        {
            var client = new SendGridClient(_apiKey);
            
            // Create a minimal test message
            var msg = new SendGridMessage();
            msg.SetFrom(new EmailAddress("test@example.com"));
            msg.AddTo(new EmailAddress("test@example.com"));
            msg.SetSubject("Test SendGrid Configuration");
            msg.AddContent(MimeType.Text, "This is a test message to verify the SendGrid configuration.");

            var response = await client.SendEmailAsync(msg);
            return response.StatusCode == System.Net.HttpStatusCode.Accepted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify SendGrid configuration");
            return false;
        }
    }

    /// <inheritdoc />
    public string ProviderName => "SendGrid";
}