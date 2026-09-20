// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker.Connections;

/// <summary>
/// Parses the plugin's <c>archon.positions.drain</c> reply. It has exactly the contract of <c>archon.events.drain</c>
/// (envelope, boot id, cursor, lost/reset, sequence numbers) with the items under <c>samples</c> instead of
/// <c>events</c>, so it is the same strict parser pointed at that array. <see cref="ArchonEventsDrain.EventsJson"/>
/// then holds the samples array.
/// </summary>
public static class ArchonPositionsParser
{
    public static bool TryParse(string? message, out ArchonEventsDrain? drain, out string? failure) =>
        ArchonEventsParser.TryParse(message, "samples", out drain, out failure);
}
