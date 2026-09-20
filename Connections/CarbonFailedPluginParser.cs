// Copyright ©2026 Scott Blomfield

using System.Text.RegularExpressions;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Worker.Connections;

/// <summary>
/// Reads the "failed plugins" section at the end of Carbon's <c>c.plugins</c> reply: the plugin files that did not compile, and why.
/// </summary>
/// <remarks>
/// <para>
/// The section, as Carbon prints it (measured on a live server):
/// </para>
/// <code>
/// *  unloaded plugins (0)
/// *  failed plugins (4)   line      stacktrace
///    BotReSpawn.cs        1436:59   Cannot implicitly convert type 'System.Func&lt;Item, int, bool&gt;' to '...'
///    CopyPaste.cs         3645:31   'Sprinkler' does not contain a definition for 'TurnOn' and no accessible extension method 'TurnOn' accepting a first argument of type 'Sprinkler' coul...
///                                   d be found (are you missing a using directive or an assembly reference?)
/// </code>
/// <para>
/// A row is a file, a <c>line:column</c> and the compiler's message. A long message is cut at the column edge with <c>...</c> and carried on the
/// next line, indented to the message column; those pieces are joined back into one message (the <c>...</c> is dropped and the pieces meet
/// exactly, which is how Carbon wrapped it). Nothing here is trusted to be well formed: a line that is neither a row nor a continuation is ignored,
/// and the section is bounded (see <see cref="MaxFailures"/> and <see cref="MaxMessageLength"/>) so a strange reply cannot make a large message.
/// </para>
/// </remarks>
public static partial class CarbonFailedPluginParser
{
    /// <summary>The most reasons kept for one server. Far more than a real server has; the guard is against a runaway reply.</summary>
    public const int MaxFailures = 100;

    /// <summary>The longest message kept for one reason.</summary>
    public const int MaxMessageLength = 500;

    /// <summary>
    /// Reads the section. Returns <c>true</c> when the reply has one (with an empty <paramref name="failures"/> for "failed plugins (0)"), and
    /// <c>false</c> when it does not - not a Carbon list, or an older Carbon without the section - so the caller can tell "none failed" from "not told".
    /// </summary>
    public static bool TryParse(string? message, out IReadOnlyList<ServerPluginFailure> failures)
    {
        failures = [];
        if (string.IsNullOrEmpty(message) || !message.Contains("failed plugins", StringComparison.Ordinal))
        {
            return false;
        }

        var lines = message.Split('\n');
        var start = Array.FindIndex(lines, l => HeaderRegex().IsMatch(l));
        if (start < 0)
        {
            return false;
        }

        var list = new List<ServerPluginFailure>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r', ' ', '\t');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('*'))
            {
                break;      // the next section
            }

            // A continuation is indented well past the file column (a row's file starts at the left), and needs a row above it to carry on from.
            // Told apart by the indent first, because a wrapped message may itself contain something shaped like "12:34".
            var indent = line.Length - line.TrimStart().Length;
            if (indent >= ContinuationIndent)
            {
                if (list.Count > 0)
                {
                    var previous = list[^1];
                    var text = line.TrimStart();
                    var joined = previous.Message.EndsWith("...", StringComparison.Ordinal)
                        ? previous.Message[..^3] + text
                        : previous.Message + " " + text;
                    list[^1] = previous with { Message = joined };
                }

                continue;
            }

            var row = RowRegex().Match(line);
            if (row.Success)
            {
                if (list.Count >= MaxFailures)
                {
                    break;
                }

                list.Add(new ServerPluginFailure(
                    row.Groups["file"].Value.Trim(), Number(row.Groups["line"].Value), Number(row.Groups["col"].Value), row.Groups["msg"].Value.Trim()));
            }
        }

        failures = list.Select(f => f with { Message = Limit(f.Message) }).ToList();
        return true;
    }

    // The message column starts well to the right of the file name; anything less indented than this is not a wrapped message.
    private const int ContinuationIndent = 20;

    private static int Number(string digits) => int.TryParse(digits, out var value) ? value : 0;

    private static string Limit(string message) =>
        message.Length <= MaxMessageLength ? message : message[..(MaxMessageLength - 3)] + "...";

    // "*  failed plugins (4)   line      stacktrace"
    [GeneratedRegex(@"^\*\s+failed plugins\s*\(\d+\)")]
    private static partial Regex HeaderRegex();

    // "   BotReSpawn.cs        1436:59   Cannot implicitly convert ..."
    [GeneratedRegex(@"^\s+(?<file>\S.*?)\s+(?<line>\d+):(?<col>\d+)\s+(?<msg>\S.*)$")]
    private static partial Regex RowRegex();
}
