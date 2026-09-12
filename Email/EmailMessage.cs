using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace RustArchon.Worker.Email;

/// <summary>
/// Represents an email message to be sent.
/// </summary>
public class EmailMessage
{
    /// <summary>
    /// Gets or sets the recipient email address.
    /// </summary>
    [Required]
    public string To { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sender email address.
    /// </summary>
    [Required]
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the subject of the email.
    /// </summary>
    [Required]
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the body content of the email.
    /// </summary>
    [Required]
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the email body is HTML formatted.
    /// </summary>
    public bool IsHtml { get; set; }

    /// <summary>
    /// Gets or sets the CC recipients.
    /// </summary>
    public IEnumerable<string> Cc { get; set; } = new List<string>();

    /// <summary>
    /// Gets or sets the BCC recipients.
    /// </summary>
    public IEnumerable<string> Bcc { get; set; } = new List<string>();

    /// <summary>
    /// Gets or sets any attachments for the email.
    /// </summary>
    public IEnumerable<EmailAttachment> Attachments { get; set; } = new List<EmailAttachment>();
}