// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;

namespace RustArchon.Worker.Connections;

/// <summary>How often the console record keeps the answer to one kind of background poll.</summary>
public enum PollStorage
{
    /// <summary>
    /// One answer every <see cref="BackgroundFrameSampler.SampleEvery"/>, whatever it says. For polls whose answer is different every time
    /// (server info, stats) and whose data is stored in its own table anyway: the console record only has to show the poll is working.
    /// </summary>
    Sampled,

    /// <summary>
    /// An answer when it differs from the last one stored, and otherwise one every <see cref="BackgroundFrameSampler.HeartbeatEvery"/>. For polls
    /// whose answer is the same until something changes (the plugin list, the plugin's own status, a tool cupboard page).
    /// </summary>
    OnChange
}

/// <summary>
/// What the Worker attaches to a command it sends for its own bookkeeping, in place of <c>RconCommandContext.Background</c>: it says the same thing
/// (nobody typed this) and also which poll it is and how much of its answers to keep.
/// </summary>
/// <param name="Key">Which poll this is. Answers are compared and counted per key.</param>
/// <param name="Storage">How much of its answers the console record keeps.</param>
public sealed record BackgroundPoll(string Key, PollStorage Storage);

/// <summary>
/// Decides which of a background poll's non-empty answers are kept in the console record. The Worker asks a server for its info, its plugin
/// state and its data every few seconds to a minute; each answer's <b>data</b> is stored in a table of its own, so the console record's copy
/// only has to show that the poll happened. Keeping every one buried the lines a person cares about and grew the table by thousands of rows a
/// day per server.
/// </summary>
/// <remarks>
/// <para>
/// Nothing that matters is ever dropped: an answer carrying a stack trace, one that reports a failure, and a drain that says events were lost or
/// the buffer was reset are always kept. A person's own command is not a poll and never reaches this. One instance per server connection; it is
/// safe to call from the connection's event thread and its poll loops at once.
/// </para>
/// <para>
/// The clock is passed in on each call so tests need no real waiting.
/// </para>
/// </remarks>
public sealed class BackgroundFrameSampler
{
    /// <summary>How often a <see cref="PollStorage.Sampled"/> poll's answer is kept.</summary>
    public static readonly TimeSpan SampleEvery = TimeSpan.FromMinutes(10);

    /// <summary>How long an <see cref="PollStorage.OnChange"/> poll may go without an answer being kept, even when nothing changed.</summary>
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromMinutes(60);

    private readonly Dictionary<string, Last> _last = [];

    /// <summary>True when this answer should be stored.</summary>
    public bool ShouldStore(BackgroundPoll poll, string? message, string? stacktrace, DateTimeOffset now)
    {
        var fingerprint = (message?.Length ?? 0, message?.GetHashCode() ?? 0);
        var noteworthy = !string.IsNullOrEmpty(stacktrace) || IsNoteworthy(poll, message);

        lock (_last)
        {
            var known = _last.TryGetValue(poll.Key, out var last);
            var keep = noteworthy
                || !known
                || (poll.Storage == PollStorage.OnChange
                    ? last.Fingerprint != fingerprint || now - last.At >= HeartbeatEvery
                    : now - last.At >= SampleEvery);

            if (keep)
            {
                _last[poll.Key] = new Last(now, fingerprint);
            }

            return keep;
        }
    }

    // A failure the plugin reported, or a drain that says it lost events or was reset: the reader has to know, so never thinned.
    private static bool IsNoteworthy(BackgroundPoll poll, string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        if (message.Contains("\"ok\":false", StringComparison.Ordinal))
        {
            return true;
        }

        if (!message.Contains("\"bootId\"", StringComparison.Ordinal))
        {
            return false;
        }

        var items = poll.Key.Contains("positions", StringComparison.Ordinal) ? "samples" : "events";
        return ArchonEventsParser.TryParse(message, items, out var drain, out _) && drain is not null && (drain.Lost || drain.Reset);
    }

    private readonly record struct Last(DateTimeOffset At, (int Length, int Hash) Fingerprint);
}
