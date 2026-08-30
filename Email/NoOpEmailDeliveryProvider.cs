// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.Logging;

namespace RustArchon.Worker.Email;

/// <summary>
/// Placeholder <see cref="IEmailDeliveryProvider"/> - logs what would have been sent and "succeeds"
/// unconditionally, so the queue/retry/dead-letter mechanics around it are real and testable before a
/// real mail provider is chosen. This is why confirmation/password-reset links only show up in this
/// process's console output right now rather than an actual inbox - see the README.
/// </summary>
/// <remarks>
/// Unlike a real provider, this must never throw - there's nothing to retry here, and the whole point
/// is to exercise the pipeline's happy path without needing real credentials configured.
/// </remarks>
public class NoOpEmailDeliveryProvider(ILogger<NoOpEmailDeliveryProvider> logger) : IEmailDeliveryProvider
{
    public Task SendAsync(string to, string subject, string htmlBody, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Would send email to {To} with subject {Subject}:\n{HtmlBody}",
            to, subject, htmlBody);
        return Task.CompletedTask;
    }
}
