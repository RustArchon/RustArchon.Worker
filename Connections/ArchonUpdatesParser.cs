// Copyright ©2026 Scott Blomfield

using System.Text.Json;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Worker.Connections;

/// <summary>
/// Parses the plugin's <c>archon.updates</c> reply. Strict about the envelope and about the type of every field of every notice (one
/// bad notice fails the whole reply and nothing is published), lenient about extras so a newer plugin still parses. The text is
/// another plugin's, so lengths are bounded here as well as in the plugin.
/// </summary>
public static class ArchonUpdatesParser
{
    public const int SupportedEnvelopeVersion = 1;
    public const int SupportedFormat = 1;
    public const int MaxNotices = 500;

    public static bool TryParse(string? message, out IReadOnlyList<PluginUpdateNoticeInfo>? updates, out string? failure)
    {
        updates = null;
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
                || !version.TryGetInt32(out var v) || v != SupportedEnvelopeVersion)
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

            if (!data.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number
                || !format.TryGetInt32(out var f) || f != SupportedFormat)
            {
                failure = "unsupported update notice format";
                return false;
            }

            if (!data.TryGetProperty("updates", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                failure = "missing updates array";
                return false;
            }

            var result = new List<PluginUpdateNoticeInfo>();
            foreach (var n in list.EnumerateArray())
            {
                if (result.Count >= MaxNotices)
                {
                    failure = "too many update notices";
                    return false;
                }

                if (n.ValueKind != JsonValueKind.Object
                    || !TryString(n, "n", out var name) || name.Length == 0
                    || !TryString(n, "c", out var current)
                    || !TryString(n, "l", out var latest)
                    || !TryString(n, "u", out var url)
                    || !TryString(n, "m", out var marketplace)
                    || !TryLong(n, "f", out var first) || !TryLong(n, "s", out var last)
                    || !TryLong(n, "t", out var times))
                {
                    failure = "an update notice has a missing or mistyped field";
                    return false;
                }

                if (!TryTime(first, out var firstSeen) || !TryTime(last, out var lastSeen))
                {
                    failure = "an update notice has a time out of range";
                    return false;
                }

                result.Add(new PluginUpdateNoticeInfo(
                    name, current, latest, url, marketplace, firstSeen, lastSeen, (int)Math.Clamp(times, 0, int.MaxValue)));
            }

            updates = result;
            return true;
        }
        catch (JsonException)
        {
            // Not JSON at all: most likely "Unknown command", i.e. a plugin without the command.
            failure = "reply is not valid JSON";
            return false;
        }
        catch (InvalidOperationException)
        {
            failure = "a field has an unexpected type";
            return false;
        }
    }

    private static bool TryTime(long unixMs, out DateTimeOffset time)
    {
        time = default;
        if (unixMs < 0 || unixMs > 253402300799999L)
        {
            return false;
        }

        time = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        return true;
    }

    private static bool TryString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryLong(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }
}
