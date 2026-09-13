namespace CloudOrders.Application.Messaging;

public sealed record OutboundMessage(
    string MessageId,
    string? CorrelationId,
    string Subject,
    string ContentType,
    ReadOnlyMemory<byte> Payload);

public sealed record OutboxTraceContext(string? TraceParent, string? TraceState);

public sealed record PendingOutboxMessage(
    Guid Id,
    Guid OrderId,
    OutboundMessage Message,
    OutboxTraceContext? TraceContext);

public interface IOutboxMessageStore
{
    Task<IReadOnlyList<PendingOutboxMessage>> GetPendingAsync(
        int batchSize,
        CancellationToken cancellationToken = default);

    Task MarkPublishedAsync(
        Guid id,
        DateTimeOffset attemptedAtUtc,
        CancellationToken cancellationToken = default);

    Task RecordFailedAttemptAsync(
        Guid id,
        DateTimeOffset attemptedAtUtc,
        string failureType,
        CancellationToken cancellationToken = default);

    Task<long> CountPendingAsync(CancellationToken cancellationToken = default);
}
