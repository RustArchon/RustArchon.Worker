namespace RustArchon.Worker.Email;

/// <summary>
/// Represents SMTP settings for email delivery.
/// </summary>
public class SmtpSettings
{
    /// <summary>
    /// Gets or sets the SMTP host name.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SMTP port number.
    /// </summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// Gets or sets a value indicating whether SSL/TLS is enabled.
    /// </summary>
    public bool EnableSsl { get; set; } = true;

    /// <summary>
    /// Gets or sets the SMTP username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SMTP password.
    /// </summary>
    public string Password { get; set; } = string.Empty;
}