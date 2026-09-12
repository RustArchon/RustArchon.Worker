namespace RustArchon.Worker.Email;

/// <summary>
/// Represents an email attachment.
/// </summary>
public class EmailAttachment
{
    /// <summary>
    /// Gets or sets the file name of the attachment.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the content of the attachment as a byte array.
    /// </summary>
    public byte[] Content { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Gets or sets the MIME type of the attachment.
    /// </summary>
    public string ContentType { get; set; } = string.Empty;
}