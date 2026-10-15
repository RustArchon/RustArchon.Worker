// Copyright ©2026 Scott Blomfield

using MassTransit;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Worker.Logging;

/// <summary>Sends a <see cref="WorkerLogBatch"/> to the Api. The seam <see cref="LogForwarder"/> is tested through.</summary>
public interface ILogBatchPublisher
{
    Task PublishAsync(WorkerLogBatch batch, CancellationToken cancellationToken);
}

/// <summary>Publishes on the Worker's MassTransit bus (RabbitMQ).</summary>
public sealed class MassTransitLogBatchPublisher(IBus bus) : ILogBatchPublisher
{
    public Task PublishAsync(WorkerLogBatch batch, CancellationToken cancellationToken) => bus.Publish(batch, cancellationToken);
}
