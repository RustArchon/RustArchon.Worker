// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker.Security;

/// <summary>
/// Calls RustArchon.Api's internal (non-JWT, shared-secret-authenticated) endpoints.
/// </summary>
public interface IInternalApiClient
{
    /// <summary>
    /// Gets a server's connection details, including its decrypted RCON password.
    /// </summary>
    /// <returns>The server's details, or <c>null</c> if it's been deleted or disabled since the
    /// caller last knew about it.</returns>
    /// <remarks>
    /// Heartbeats are <b>not</b> sent through this client - a connection actor publishes
    /// <c>RustArchon.Messaging.Contracts.ServerConnectionHeartbeat</c> directly onto the message bus
    /// (see <c>ServerConnectionActor</c>), since the API's ingestion of that signal has no reason to
    /// go through a request/response HTTP round trip.
    /// </remarks>
    Task<InternalRustServerInfo?> GetServerAsync(Guid serverId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the platform's current email delivery configuration, secrets decrypted. Called fresh on
    /// every send rather than cached - see <c>InternalController.GetEmailSettings</c>'s remarks.
    /// </summary>
    Task<InternalEmailSettings> GetEmailSettingsAsync(CancellationToken cancellationToken);
}
