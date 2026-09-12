// Copyright ©2026 Scott Blomfield

using RustArchon.Worker.Security;

namespace RustArchon.Worker.Email;

/// <summary>
/// Picks which <see cref="IEmailDeliveryProvider"/> to use for one send, from settings fetched fresh
/// off RustArchon.Api for that send - see <see cref="EmailDeliveryProviderFactory"/>.
/// </summary>
/// <remarks>
/// Not a fixed DI registration the way most single-implementation interfaces in this app are - which
/// provider applies can change the moment an admin saves the settings page, and a Worker instance has
/// no push notification of that, so the choice has to be made per-send from freshly-fetched settings
/// rather than decided once at startup. See <c>Program.cs</c>'s remarks on why
/// <c>AddSingleton&lt;IEmailDeliveryProvider, ...&gt;</c> was replaced with this.
/// </remarks>
public interface IEmailDeliveryProviderFactory
{
    /// <summary>Builds the provider <paramref name="settings"/> currently calls for.</summary>
    IEmailDeliveryProvider Resolve(InternalEmailSettings settings);
}
