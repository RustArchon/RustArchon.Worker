// Copyright ©2026 Scott Blomfield

namespace RustArchon.Worker.Connections;

/// <summary>
/// Recognizes a background poll's answer that says "nothing": no text at all, an empty list (the player list of a server nobody is on),
/// or a plugin drain that reports no new items. The Worker asks every few seconds; on a quiet server nearly every answer is one of
/// these, and storing each of them as a console row buries the real lines and grows the table for no reason.
/// </summary>
public static class EmptyPollReply
{
    /// <summary>
    /// True when <paramref name="message"/> is nothing, <c>[]</c>, or a well-formed <c>archon.events.drain</c> / <c>archon.positions.drain</c>
    /// reply with no items that also reports nothing lost or reset. Anything else - an error, an unknown command, a reply carrying
    /// data, a list with something in it, or a reply that says events were lost - is <b>not</b> empty and is stored like any other frame.
    /// Cheap for the common case: a message that is neither blank nor a short list is turned away before any JSON is read unless it
    /// contains a boot id.
    /// </summary>
    public static bool IsEmpty(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

        var trimmed = message.AsSpan().Trim();
        if (trimmed.Length <= 8 && IsEmptyList(trimmed))
        {
            return true;
        }

        if (!message.Contains("\"bootId\"", StringComparison.Ordinal))
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

    // "[]", tolerating whitespace inside the brackets.
    private static bool IsEmptyList(ReadOnlySpan<char> text)
    {
        if (text.Length < 2 || text[0] != '[' || text[^1] != ']')
        {
            return false;
        }

        return text[1..^1].Trim().IsEmpty;
    }
}
