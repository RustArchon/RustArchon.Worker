// Copyright ©2026 Scott Blomfield

using System.Text.Json;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Worker.Connections;

/// <summary>What the RustArchon plugin said about itself in reply to <c>archon.hello</c>.</summary>
public sealed record ArchonHello(
    int ProtocolVersion,
    string PluginVersion,
    IReadOnlyList<string> Capabilities,
    bool RecordingEnabled,
    bool CombatLogEnabled,
    bool SettingsPersisted,
    string SigningState = ArchonHelloParser.UnknownSigningState,
    string SigningKeyFingerprint = "");

/// <summary>
/// Parses the plugin's <c>archon.hello</c> reply - one JSON envelope <c>{"v":1,"ok":true,"data":{...}}</c>.
/// </summary>
/// <remarks>
/// Deliberately strict about the envelope and lenient about everything else: an unknown envelope version,
/// <c>ok: false</c>, or a missing/mistyped required field is a failure (the panel must not enable a feature
/// on a reply it did not understand), while extra properties are ignored so a newer plugin that adds fields
/// still parses. A server that has no plugin answers with plain "Unknown command" text, which fails the same
/// way instead of throwing.
/// </remarks>
public static class ArchonHelloParser
{
    /// <summary>The plugin name as it appears in <c>o.plugins</c> / <c>c.plugins</c>.</summary>
    public const string PluginName = RustArchonPlugin.Name;

    public const int SupportedEnvelopeVersion = 1;

    /// <summary>What a plugin build too old to report its signing check is recorded as.</summary>
    public const string UnknownSigningState = PluginSigningStates.Unknown;

    /// <summary>Whether a parsed plugin list positively shows the RustArchon plugin loaded.</summary>
    public static bool IsPluginListed(IReadOnlyList<ServerPluginInfo> plugins) =>
        RustArchonPlugin.IsListed(plugins.Select(p => p.Name));

    public static bool TryParse(string? message, out ArchonHello? hello, out string? failure)
    {
        hello = null;
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
                var reason = root.TryGetProperty("err", out var err) && err.ValueKind == JsonValueKind.String
                    ? err.GetString()
                    : "no error given";
                failure = $"plugin reported an error: {reason}";
                return false;
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                failure = "missing data";
                return false;
            }

            if (!data.TryGetProperty("protocolVersion", out var protocol) || protocol.ValueKind != JsonValueKind.Number
                || !data.TryGetProperty("version", out var pluginVersion) || pluginVersion.ValueKind != JsonValueKind.String
                || !data.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array
                || !data.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object
                || !TryGetBool(settings, "recording", out var recording)
                || !TryGetBool(settings, "combat", out var combat))
            {
                failure = "a required field is missing or has the wrong type";
                return false;
            }

            var capabilityNames = new List<string>();
            foreach (var capability in capabilities.EnumerateArray())
            {
                if (capability.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(capability.GetString()))
                {
                    capabilityNames.Add(capability.GetString()!);
                }
            }

            // Absent means "unknown", which must not read as "persisted".
            var persisted = TryGetBool(settings, "persisted", out var persistedValue) && persistedValue;

            // Optional: a build from before signing existed does not send it. Lenient on purpose - a missing or odd
            // "signing" block reads as unknown and never fails the whole handshake.
            var signingState = UnknownSigningState;
            var signingFingerprint = "";
            if (data.TryGetProperty("signing", out var signing) && signing.ValueKind == JsonValueKind.Object)
            {
                if (signing.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(state.GetString()))
                {
                    signingState = PluginSigningStates.Normalize(state.GetString());
                }

                if (signing.TryGetProperty("keyFingerprint", out var fingerprint) && fingerprint.ValueKind == JsonValueKind.String)
                {
                    signingFingerprint = PluginSigningStates.NormalizeFingerprint(fingerprint.GetString());
                }
            }

            hello = new ArchonHello(
                protocol.GetInt32(), pluginVersion.GetString()!, capabilityNames, recording, combat, persisted,
                signingState, signingFingerprint);
            return true;
        }
        catch (JsonException)
        {
            failure = "reply is not valid JSON";
            return false;
        }
        catch (FormatException)
        {
            failure = "a number was out of range";
            return false;
        }
    }

    private static bool TryGetBool(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                return true;
            default:
                return false;
        }
    }
}
