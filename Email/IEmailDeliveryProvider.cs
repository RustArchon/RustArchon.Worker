using System.Threading.Tasks;

namespace RustArchon.Worker.Email;

/// <summary>
/// Defines the contract for email delivery providers.
/// </summary>
public interface IEmailDeliveryProvider
{
    /// <summary>
    /// Sends an email message asynchronously.
    /// </summary>
    /// <param name="message">The email message to send.</param>
    /// <returns>True if the email was sent successfully, false otherwise.</returns>
    Task<bool> SendEmailAsync(EmailMessage message);

    /// <summary>
    /// Verifies the current configuration with the email provider.
    /// </summary>
    /// <returns>True if the configuration is valid and working, false otherwise.</returns>
    Task<bool> VerifyConfigurationAsync();

    /// <summary>
    /// Gets the name of the email provider.
    /// </summary>
    string ProviderName { get; }
}