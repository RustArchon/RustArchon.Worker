// Copyright ©2026 Scott Blomfield

using RustArchon.Messaging.Contracts;
using RustArchon.Rcon.Containers;
using RustArchon.Rcon.Messages;
using RustArchon.Rcon.Parsers;

namespace RustArchon.Worker.Connections;

/// <summary>
/// Turns the raw text of an <c>o.plugins</c> / <c>c.plugins</c> response into the
/// <see cref="ServerPluginInfo"/> rows <see cref="ServerPluginsCaptured"/> carries, via
/// <c>RustArchon.Rcon</c>'s own <see cref="OxidePluginListParser"/> / <see cref="CarbonPluginListParser"/>.
/// </summary>
/// <remarks>
/// A response that isn't a plugin list at all - the "Unknown command" reply a server without that
/// framework gives - parses to an empty list rather than throwing, which is exactly what
/// <c>ServerConnectionActor</c>'s probe order relies on to tell the frameworks apart.
/// </remarks>
public static class ServerPluginListParser
{
    public static IReadOnlyList<ServerPluginInfo> Parse(ServerModFramework framework, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return [];
        }

        var response = new WebRconResponse { Message = message };

        switch (framework)
        {
            case ServerModFramework.Oxide:
                new OxidePluginListParser(_ => { }).TryParseMessage(response, out var oxide);
                return oxide is OxidePluginList oxideList
                    ? oxideList.Plugins.Select(p => new ServerPluginInfo(p.Name, p.Author, p.Version)).ToList()
                    : [];

            case ServerModFramework.Carbon:
                new CarbonPluginListParser(_ => { }).TryParseMessage(response, out var carbon);
                return carbon is CarbonPluginList carbonList
                    ? carbonList.Plugins
                        .Where(p => p.Package.Length > 0)
                        .Select(p => new ServerPluginInfo(p.Package, p.Author, p.Version))
                        .ToList()
                    : [];

            default:
                return [];
        }
    }
}
