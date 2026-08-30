// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker.Connections;

/// <summary>
/// Tracks which servers this worker instance currently owns a connection actor for.
/// </summary>
public interface IConnectionSupervisor
{
    /// <summary>Gets the connection actor for a server, if this instance currently owns one.</summary>
    bool TryGetActor(Guid serverId, out ServerConnectionActor actor);

    /// <summary>
    /// Starts a connection actor for a server, tearing down and replacing any existing one for the
    /// same id first (used both for a brand-new claim and for refreshing an existing connection with
    /// updated credentials).
    /// </summary>
    Task StartOrRestartAsync(Guid serverId, Guid tenantId, string host, int port, string rconPassword, CancellationToken cancellationToken);

    /// <summary>Stops and removes the connection actor for a server, if this instance owns one.</summary>
    Task StopAsync(Guid serverId);
}
