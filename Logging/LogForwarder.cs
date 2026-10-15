// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RustArchon.Diagnostics;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Worker.Logging;

/// <summary>
/// Sends this Worker's buffered log entries to the Api in batches (<see cref="WorkerLogBatch"/>, ADR-0006), so they can be read through the
/// diagnostic log endpoint.
/// </summary>
/// <remarks>
/// Diagnostics must never slow or break the Worker, so this is deliberately lossy: the position is advanced <em>before</em> publishing, a batch
/// that fails to publish is dropped rather than retried, and the Worker's sequence numbers (which the Api checks for gaps) are what make that loss
/// visible downstream. A failure is logged as a warning at most once a minute so a broker outage cannot itself flood the log.
/// </remarks>
public sealed class LogForwarder(LogRing ring, ILogBatchPublisher publisher, ILogger<LogForwarder> logger, TimeProvider? timeProvider = null)
    : BackgroundService
{
    public const int MaxEntriesPerBatch = 200;

    public const int MaxCharactersPerBatch = 256 * 1024;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);

    // Batches sent back to back in one tick when a backlog has built up, so catching up does not take one interval per batch.
    private const int MaxBatchesPerTick = 10;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _cursor;
    private long _droppedSinceWarning;
    private DateTimeOffset _lastWarningUtc = DateTimeOffset.MinValue;

    /// <summary>Publishes one batch of whatever is new. Returns how many entries were sent (0 when there was nothing, or the publish failed).</summary>
    public async Task<int> ForwardOnceAsync(CancellationToken cancellationToken)
    {
        var page = ring.ReadAfter(ring.InstanceId, _cursor, MaxEntriesPerBatch);
        if (page.Entries.Count == 0)
        {
            _cursor = Math.Max(_cursor, page.ReturnedThrough);
            return 0;
        }

        var entries = new List<WorkerLogEntry>(page.Entries.Count);
        var characters = 0;
        foreach (var entry in page.Entries)
        {
            var size = entry.Message.Length + (entry.Exception?.Length ?? 0);
            if (entries.Count > 0 && characters + size > MaxCharactersPerBatch)
            {
                break;
            }

            characters += size;
            entries.Add(new WorkerLogEntry(
                entry.Seq, entry.TimestampUtc, (int)entry.Level, entry.Category, entry.Message, entry.Exception, entry.Properties));
        }

        // At most once: move on before sending, so a failing broker cannot make this resend the same entries forever.
        _cursor = entries[^1].Seq;

        try
        {
            await publisher.PublishAsync(new WorkerLogBatch(ring.InstanceId, entries), cancellationToken);
            return entries.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _droppedSinceWarning++;
            WarnOncePerMinute(ex);
            return 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        try
        {
            do
            {
                for (var i = 0; i < MaxBatchesPerTick; i++)
                {
                    if (await ForwardOnceAsync(stoppingToken) == 0 || ring.Head <= _cursor)
                    {
                        break;
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down; whatever is still buffered is only ever in memory anyway.
        }
    }

    private void WarnOncePerMinute(Exception ex)
    {
        var now = _time.GetUtcNow();
        if (now - _lastWarningUtc < WarningInterval)
        {
            return;
        }

        _lastWarningUtc = now;
        logger.LogWarning(
            ex,
            "Could not publish {Dropped} log batch(es) to the message bus; those entries are missing from the diagnostic log endpoint.",
            _droppedSinceWarning);
        _droppedSinceWarning = 0;
    }
}
