// Copyright ©2026 Scott Blomfield

using System.Text.Json;

namespace RustArchon.Worker.Connections;

/// <summary>One reply to <c>archon.events.drain</c>.</summary>
/// <param name="Head">The newest sequence number the plugin has assigned; the Worker is caught up when its cursor reaches it.</param>
/// <param name="Cursor">The last sequence number in this reply (or the one asked for, when nothing was new): what to send next time.</param>
/// <param name="EventsJson">The <c>events</c> array as raw JSON, untouched.</param>
public sealed record ArchonEventsDrain(
    long BootId,
    long Head,
    long Cursor,
    bool Lost,
    bool Reset,
    int Count,
    long FirstSequence,
    long LastSequence,
    string EventsJson);

/// <summary>
/// Parses the plugin's <c>archon.events.drain</c> reply. Strict about the envelope and the fields the Worker acts
/// on (a wrong type or an unknown format is a failure, and the batch is not stored), lenient about extras so a newer
/// plugin that adds fields still parses. The events themselves are passed on as raw JSON, not interpreted here.
/// </summary>
public static class ArchonEventsParser
{
    public const int SupportedEnvelopeVersion = 1;
    public const int SupportedFormat = 1;

    public static bool TryParse(string? message, out ArchonEventsDrain? drain, out string? failure) =>
        TryParse(message, "events", out drain, out failure);

    /// <summary>The same reply with its items under <paramref name="itemsProperty"/> ("samples" for position drains).</summary>
    public static bool TryParse(string? message, string itemsProperty, out ArchonEventsDrain? drain, out string? failure)
    {
        drain = null;
        failure = null;

        if (string.IsNullOrWhiteSpace(message))
        {
            failure = "empty reply";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                failure = "reply is not a JSON object";
                return false;
            }

            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != SupportedEnvelopeVersion)
            {
                failure = "unsupported or missing envelope version";
                return false;
            }

            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var reason = root.TryGetProperty("err", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : "no error given";
                failure = $"plugin reported an error: {reason}";
                return false;
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                failure = "missing data";
                return false;
            }

            if (!TryLong(data, "format", out var format) || format != SupportedFormat)
            {
                failure = "unsupported event format";
                return false;
            }

            if (!TryLong(data, "bootId", out var bootId) || bootId < 1
                || !TryLong(data, "head", out var head) || head < 0
                || !TryLong(data, "cursor", out var cursor) || cursor < 0
                || !TryBool(data, "lost", out var lost)
                || !TryBool(data, "reset", out var reset))
            {
                failure = "missing or mistyped bootId, head, cursor, lost or reset";
                return false;
            }

            if (!data.TryGetProperty(itemsProperty, out var events) || events.ValueKind != JsonValueKind.Array)
            {
                failure = $"missing {itemsProperty} array";
                return false;
            }

            var count = 0;
            long first = 0;
            long last = 0;
            foreach (var e in events.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || !TryLong(e, "s", out var sequence) || sequence < 1)
                {
                    failure = "an event has no valid sequence number";
                    return false;
                }

                if (count == 0) { first = sequence; }
                else if (sequence <= last)
                {
                    failure = "events are not in increasing sequence order";
                    return false;
                }

                last = sequence;
                count++;
            }

            if (count > 0 && last > cursor)
            {
                failure = "the cursor is behind the last event";
                return false;
            }

            drain = new ArchonEventsDrain(bootId, head, cursor, lost, reset, count, first, last, events.GetRawText());
            return true;
        }
        catch (JsonException)
        {
            // Not JSON at all: most likely "Unknown command", i.e. an older plugin without the command.
            failure = "reply is not valid JSON";
            return false;
        }
        catch (InvalidOperationException)
        {
            failure = "a field has an unexpected type";
            return false;
        }
        catch (FormatException)
        {
            failure = "a field has an unexpected type";
            return false;
        }
    }

    private static bool TryLong(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    private static bool TryBool(JsonElement parent, string name, out bool value)
    {
        value = false;
        if (!parent.TryGetProperty(name, out var element)) { return false; }

        switch (element.ValueKind)
        {
            case JsonValueKind.True: value = true; return true;
            case JsonValueKind.False: value = false; return true;
            default: return false;
        }
    }
}
