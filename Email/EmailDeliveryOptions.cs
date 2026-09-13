// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker.Email;

/// <summary>
/// Process-wide email delivery override, read once at startup from
/// <c>RUSTARCHON_SUPPRESS_EMAIL_DELIVERY</c> (see <c>Program.cs</c>) - unlike
/// <see cref="Security.InternalEmailSettings"/> (the admin-configurable platform setting this worker
/// re-fetches fresh on every send), this is a deployment-level kill switch that takes precedence over
/// whatever provider the platform's own settings currently name.
/// </summary>
/// <remarks>
/// Exists for a test/staging deployment that still wants a real provider configured in
/// <see cref="Security.InternalEmailSettings"/> - to exercise the settings UI and DTOs end to end -
/// without risking a real send. Confirmed the hard way: a staging instance with a real SMTP provider
/// configured went on to actually email a batch of test-account invoices; the addresses were fake and
/// every one bounced, but the sends themselves were real. See
/// <see cref="EmailDeliveryProviderFactory"/>'s remarks for how this is applied.
/// </remarks>
/// <param name="SuppressDelivery">
/// When true, <see cref="EmailDeliveryProviderFactory.Resolve"/> always returns a
/// <see cref="SuppressedEmailDeliveryProvider"/>, regardless of what <see cref="Security.InternalEmailSettings"/>
/// says.
/// </param>
public record EmailDeliveryOptions(bool SuppressDelivery);
