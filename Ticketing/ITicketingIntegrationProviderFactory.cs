// Copyright ©2026 Scott Blomfield

using RustArchon.Worker.Security;

namespace RustArchon.Worker.Ticketing;

/// <summary>Resolves which <see cref="ITicketingIntegrationProvider"/> a ticket event should be
/// mirrored through, from the platform's current settings.</summary>
public interface ITicketingIntegrationProviderFactory
{
    ITicketingIntegrationProvider Resolve(InternalTicketingSettings settings);
}
