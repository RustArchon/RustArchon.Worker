using System.ComponentModel.DataAnnotations;

namespace RustArchon.Worker.Email;

/// <summary>
/// Configuration settings for email delivery providers.
/// </summary>
public class EmailConfiguration
{
    /// <summary>
    /// Gets or sets the type of email provider to use.
    /// </summary>
    public string ProviderType { get; set; } = "None";

    /// <summary>
    /// Gets or sets the SMTP host name.
    /// </summary>
    public string SmtpHost { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SMTP port number.
    /// </summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>
    /// Gets or sets a value indicating whether SSL/TLS is enabled for SMTP.
    /// </summary>
    public bool SmtpEnableSsl { get; set; } = true;

    /// <summary>
    /// Gets or sets the SMTP username.
    /// </summary>
    public string SmtpUsername { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SMTP password (encrypted in storage).
    /// </summary>
    [DataType(DataType.Password)]
    public string SmtpPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SendGrid API key.
    /// </summary>
    [DataType(DataType.Password)]
    public string SendGridApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the default sender address.
    /// </summary>
    public string DefaultFromAddress { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the default sender name.
    /// </summary>
    public string DefaultFromName { get; set; } = string.Empty;
}