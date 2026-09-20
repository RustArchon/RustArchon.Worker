// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker.Connections;

/// <summary>
/// Recognizes the reply a plugin gives to a drain poll when nothing happened since the last one. The Worker asks every few
/// seconds; on a quiet server nearly every answer is "no new events", and storing each of those as a console row buries the
/// real lines and grows the table for no reason.
/// </summary>
public static class EmptyDrainReply
{
    /// <summary>
    /// True when <paramref name="message"/> is a well-formed <c>archon.events.drain</c> or <c>archon.positions.drain</c> reply with no
    /// items that also reports nothing lost or reset. Anything else - an error, an unknown command, a reply carrying data, or a
    /// reply that says events were lost - is <b>not</b> empty and is stored like any other frame. Cheap for the common non-drain case:
    /// a message without a boot id is turned away before any JSON is read.
    /// </summary>
    public static bool IsEmpty(string? message)
    {
        if (string.IsNullOrEmpty(message) || !message.Contains("\"bootId\"", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var items in new[] { "events", "samples" })
        {
            if (ArchonEventsParser.TryParse(message, items, out var drain, out _) && drain is not null)
            {
                return drain.Count == 0 && !drain.Lost && !drain.Reset;
            }
        }

        return false;
    }
}
