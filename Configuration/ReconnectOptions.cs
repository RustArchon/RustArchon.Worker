// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker.Configuration;

/// <summary>
/// Reconnect tuning passed through to each server's <c>RustArchon.Rcon.RustWebRconClient</c>, bound
/// from the <c>Reconnect</c> configuration section.
/// </summary>
/// <remarks>
/// <see cref="ErrorReconnectTimeout"/> is only the <em>starting</em> delay - <c>RustWebRconClient</c>
/// now doubles it on each consecutive failure, up to <see cref="MaxErrorReconnectTimeout"/>, and resets
/// back to this value the moment a connection actually succeeds (see that class's own remarks). Not
/// Websocket.Client's own behavior - the library itself implements no backoff at all, only a fixed
/// retry interval; RustWebRconClient mutates the library's ErrorReconnectTimeout property itself,
/// confirmed by hand that the library reads it fresh on every reconnect it schedules rather than
/// capturing it once. Leave either value <c>null</c> to accept Websocket.Client's own default
/// (1 minute) as the starting point.
/// </remarks>
public class ReconnectOptions
{
    /// <summary>How long a connection can go without receiving any message before it's treated as
    /// dead and reconnected.</summary>
    public TimeSpan? ReconnectTimeout { get; set; }

    /// <summary>The starting delay between reconnect attempts after a connection error - see the class
    /// remarks for why this isn't the only delay that's ever actually used.</summary>
    public TimeSpan? ErrorReconnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The cap the backoff above never exceeds, however many consecutive failures there have
    /// been. Defaults to RustWebRconClient's own default (5 minutes) when left null.</summary>
    public TimeSpan? MaxErrorReconnectTimeout { get; set; }
}
