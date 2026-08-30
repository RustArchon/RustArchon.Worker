// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker.Email;

/// <summary>
/// The actual "talk to a mail provider" seam - deliberately separate from
/// <c>Messaging.EmailRequestedConsumer</c>, which owns the queue/retry mechanics and shouldn't need to
/// change when the provider does.
/// </summary>
/// <remarks>
/// No provider has been chosen yet - <see cref="NoOpEmailDeliveryProvider"/> is registered by default
/// (see <c>Program.cs</c>) and just logs. Swap the DI registration for a real implementation (SMTP,
/// SendGrid, etc.) before production - see the README. Throwing from <see cref="SendAsync"/> is how a
/// real implementation reports a failed send: <c>EmailRequestedConsumer</c> lets the exception
/// propagate so MassTransit's retry policy (and eventual dead-letter queue, if retries are exhausted)
/// handles it - don't swallow failures here.
/// </remarks>
public interface IEmailDeliveryProvider
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken cancellationToken);
}
