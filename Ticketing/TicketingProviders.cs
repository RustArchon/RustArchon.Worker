// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker.Ticketing;

/// <summary>
/// The values <see cref="Security.InternalTicketingSettings.Provider"/> can hold - mirrors
/// <c>RustArchon.Api.Infrastructure.PlatformSettingsRegistry.TicketingProviders</c> exactly, the same
/// duplicated-rather-than-shared reasoning as <c>Email.EmailProviders</c>.
/// </summary>
public static class TicketingProviders
{
    public const string Internal = "Internal";
    public const string Webhook = "Webhook";
}
