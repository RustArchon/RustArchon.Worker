// Copyright ©2026 Scott Blomfield

using System.Text.Json;

namespace RustArchon.Worker.Connections;

/// <summary>One reply to <c>archon.tcs</c>: a page of the plugin's tool cupboard index.</summary>
/// <param name="Total">How many cupboards the plugin has indexed in all.</param>
/// <param name="Next">Where the next page starts; the whole list has been read once this reaches <paramref name="Total"/>.</param>
/// <param name="TcsRaw">The cupboards on this page, each as its own raw JSON, untouched.</param>
public sealed record ArchonTcsPage(bool Ready, int Total, int Offset, int Next, IReadOnlyList<string> TcsRaw);

/// <summary>
/// Parses the plugin's <c>archon.tcs</c> reply. Strict about the envelope and the fields the Worker acts on, lenient
/// about extras; a reply it does not fully understand is dropped whole, so nothing wrong reaches the database.
/// </summary>
public static class ArchonTcsParser
{
    public const int SupportedEnvelopeVersion = 1;
    public const int SupportedFormat = 1;

    public static bool TryParse(string? message, out ArchonTcsPage? page, out string? failure)
    {
        page = null;
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

            if (!TryInt(data, "format", out var format) || format != SupportedFormat)
            {
                failure = "unsupported format";
                return false;
            }

            if (!data.TryGetProperty("ready", out var ready) || (ready.ValueKind != JsonValueKind.True && ready.ValueKind != JsonValueKind.False)
                || !TryInt(data, "total", out var total) || total < 0
                || !TryInt(data, "offset", out var offset) || offset < 0
                || !TryInt(data, "next", out var next) || next < offset)
            {
                failure = "missing or mistyped ready, total, offset or next";
                return false;
            }

            if (!data.TryGetProperty("tcs", out var tcs) || tcs.ValueKind != JsonValueKind.Array)
            {
                failure = "missing tcs array";
                return false;
            }

            var raw = new List<string>();
            foreach (var tc in tcs.EnumerateArray())
            {
                if (tc.ValueKind != JsonValueKind.Object)
                {
                    failure = "a cupboard is not an object";
                    return false;
                }

                raw.Add(tc.GetRawText());
            }

            page = new ArchonTcsPage(ready.ValueKind == JsonValueKind.True, total, offset, next, raw);
            return true;
        }
        catch (JsonException)
        {
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

    private static bool TryInt(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }
}
