// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;

namespace RustArchon.Worker.Ticketing;

/// <summary>
/// Mirrors a ticket event out to whatever external system the site owner has configured - the seam a
/// future named vendor integration (Zendesk, etc.) plugs into. See
/// <see cref="InternalNoOpTicketingIntegrationProvider"/> (the default, nothing configured) and
/// <see cref="WebhookTicketingIntegrationProvider"/> (the one other option that exists today).
/// </summary>
public interface ITicketingIntegrationProvider
{
    Task NotifyTicketCreatedAsync(Guid ticketId);

    Task NotifyTicketMessageAddedAsync(Guid ticketId, Guid messageId);
}
