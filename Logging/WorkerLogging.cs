// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.Logging;

namespace RustArchon.Worker.Logging;

public static class WorkerLogging
{
    /// <summary>Entries the Worker's log buffer holds.</summary>
    public const int BufferCapacity = 5000;

    /// <summary>
    /// Left out of the buffer: the message bus's own routine chatter. Publishing a log batch makes the bus log, and capturing that would give the
    /// forwarder something new to forward after every batch, forever. Warnings and errors from the bus are kept; they are what a broken broker
    /// looks like. Leaving entries out here (rather than skipping them when forwarding) keeps the sequence numbers contiguous, so the Api can
    /// trust a gap to mean loss.
    /// </summary>
    public static bool ExcludeFromCapture(string category, LogLevel level) =>
        level < LogLevel.Warning && category.StartsWith("MassTransit", StringComparison.Ordinal);
}
