// Copyright ©2026 Scott Blomfield

using System.Text.Json;

namespace RustArchon.Worker.Connections;

/// <summary>One reply to <c>archon.map.status</c>.</summary>
/// <param name="WorldKnown">False until the game has finished loading the world; nothing else is meaningful then.</param>
/// <param name="UploadState">The plugin's upload state (<c>idle</c> when it has never been asked).</param>
public sealed record ArchonMapStatus(
    bool WorldKnown, int WorldSize, long WorldSeed, string FileName, bool Exists, long Bytes, string UploadState);

/// <summary>
/// Parses the plugin's <c>archon.map.status</c> and <c>archon.map.monuments</c> replies. Strict about the envelope and the
/// fields the Worker acts on (a wrong type is a failure and nothing is published), lenient about extras so a newer plugin
/// still parses. The monument list is passed on as raw JSON, not interpreted here.
/// </summary>
public static class ArchonMapParser
{
    public const int SupportedEnvelopeVersion = 1;

    public static bool TryParseStatus(string? message, out ArchonMapStatus? status, out string? failure)
    {
        status = null;
        if (!TryData(message, out var document, out var data, out failure))
        {
            return false;
        }

        using (document)
        {
            try
            {
                if (!data.TryGetProperty("world", out var world) || world.ValueKind != JsonValueKind.Object
                    || !TryBool(world, "known", out var known))
                {
                    failure = "missing or mistyped world";
                    return false;
                }

                if (!TryLong(world, "size", out var size) || size < 0 || size > int.MaxValue
                    || !TryLong(world, "seed", out var seed) || seed < 0)
                {
                    failure = "missing or mistyped world size or seed";
                    return false;
                }

                if (!TryString(data, "file", out var file)
                    || !TryBool(data, "exists", out var exists)
                    || !TryLong(data, "bytes", out var bytes) || bytes < 0)
                {
                    failure = "missing or mistyped file, exists or bytes";
                    return false;
                }

                // "upload" is null until the plugin has been asked to upload once, and absent from an older build.
                var uploadState = "idle";
                if (data.TryGetProperty("upload", out var upload) && upload.ValueKind == JsonValueKind.Object)
                {
                    if (!TryString(upload, "state", out uploadState))
                    {
                        failure = "the upload block has no state";
                        return false;
                    }
                }

                status = new ArchonMapStatus(known, (int)size, seed, file, exists, bytes, uploadState);
                return true;
            }
            catch (InvalidOperationException)
            {
                failure = "a field has an unexpected type";
                return false;
            }
        }
    }

    /// <summary>The <c>monuments</c> array of <c>archon.map.monuments</c>, as raw JSON, after checking each has a name and position.</summary>
    public static bool TryParseMonuments(string? message, out string? monumentsJson, out string? failure)
    {
        monumentsJson = null;
        if (!TryData(message, out var document, out var data, out failure))
        {
            return false;
        }

        using (document)
        {
            if (!data.TryGetProperty("monuments", out var monuments) || monuments.ValueKind != JsonValueKind.Array)
            {
                failure = "missing monuments array";
                return false;
            }

            foreach (var m in monuments.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object || !TryString(m, "n", out _)
                    || !TryNumber(m, "x") || !TryNumber(m, "y") || !TryNumber(m, "z"))
                {
                    failure = "a monument has no name or position";
                    return false;
                }
            }

            monumentsJson = monuments.GetRawText();
            return true;
        }
    }

    private static bool TryData(string? message, out JsonDocument document, out JsonElement data, out string? failure)
    {
        document = null!;
        data = default;
        failure = null;

        if (string.IsNullOrWhiteSpace(message))
        {
            failure = "empty reply";
            return false;
        }

        try
        {
            var parsed = JsonDocument.Parse(message);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                parsed.Dispose();
                failure = "reply is not a JSON object";
                return false;
            }

            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v) || v != SupportedEnvelopeVersion)
            {
                parsed.Dispose();
                failure = "unsupported or missing envelope version";
                return false;
            }

            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var reason = root.TryGetProperty("err", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : "no error given";
                parsed.Dispose();
                failure = $"plugin reported an error: {reason}";
                return false;
            }

            if (!root.TryGetProperty("data", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                parsed.Dispose();
                failure = "missing data";
                return false;
            }

            document = parsed;
            data = payload;
            return true;
        }
        catch (JsonException)
        {
            failure = "reply is not valid JSON";
            return false;
        }
    }

    private static bool TryLong(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    private static bool TryNumber(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var d) && double.IsFinite(d);

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

    private static bool TryString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) { return false; }

        value = element.GetString() ?? string.Empty;
        return true;
    }
}
