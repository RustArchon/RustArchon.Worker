// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RustArchon.Worker.Ticketing;

/// <summary>
/// The default provider - the internal ticket system (Admin/Tickets, My Tickets) is already the real
/// place every ticket lives and gets answered, so "Internal" means there is nothing further to mirror
/// anywhere.
/// </summary>
public class InternalNoOpTicketingIntegrationProvider(ILogger<InternalNoOpTicketingIntegrationProvider> logger)
    : ITicketingIntegrationProvider
{
    public Task NotifyTicketCreatedAsync(Guid ticketId)
    {
        logger.LogDebug("Ticket {TicketId} created - Internal provider, nothing to mirror.", ticketId);
        return Task.CompletedTask;
    }

    public Task NotifyTicketMessageAddedAsync(Guid ticketId, Guid messageId)
    {
        logger.LogDebug(
            "Message {MessageId} added to ticket {TicketId} - Internal provider, nothing to mirror.",
            messageId, ticketId);
        return Task.CompletedTask;
    }
}
