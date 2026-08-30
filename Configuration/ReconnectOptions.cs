// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker.Configuration;

/// <summary>
/// Reconnect tuning passed through to each server's <c>RustArchon.Rcon.RustWebRconClient</c>, bound
/// from the <c>Reconnect</c> configuration section.
/// </summary>
/// <remarks>
/// This is Websocket.Client's own reconnect behavior (see <c>RustWebRconClient</c>'s constructor
/// remarks), not a backoff scheme this project implements - there is no exponential backoff here,
/// only a fixed retry interval. Leave either value <c>null</c> to accept Websocket.Client's own
/// default (1 minute for both, as of the version this project pins).
/// </remarks>
public class ReconnectOptions
{
    /// <summary>How long a connection can go without receiving any message before it's treated as
    /// dead and reconnected.</summary>
    public TimeSpan? ReconnectTimeout { get; set; }

    /// <summary>The fixed delay between reconnect attempts after a connection error.</summary>
    public TimeSpan? ErrorReconnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
