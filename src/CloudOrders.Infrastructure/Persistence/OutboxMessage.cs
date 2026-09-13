using CloudOrders.Application.Messaging;

namespace CloudOrders.Infrastructure.Persistence;

public sealed class OutboxMessage
{
    private OutboxMessage()
    {
    }

    public OutboxMessage(
        Guid id,
        Guid orderId,
        MessageMetadata metadata,
        string payloadJson,
        DateTimeOffset createdAtUtc,
        OutboxTraceContext? traceContext)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        Id = id;
        OrderId = orderId;
        MessageId = metadata.MessageId;
        CorrelationId = metadata.CorrelationId ?? throw new ArgumentException(
            "An outbox message requires a correlation ID.",
            nameof(metadata));
        Subject = metadata.Subject;
        ContentType = metadata.ContentType;
        PayloadJson = payloadJson;
        CreatedAtUtc = createdAtUtc;
        TraceParent = traceContext?.TraceParent;
        TraceState = traceContext?.TraceState;
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public string MessageId { get; private set; } = null!;

    public string CorrelationId { get; private set; } = null!;

    public string Subject { get; private set; } = null!;

    public string ContentType { get; private set; } = null!;

    public string PayloadJson { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? LastAttemptAtUtc { get; private set; }

    public string? LastErrorType { get; private set; }

    public string? TraceParent { get; private set; }

    public string? TraceState { get; private set; }
}
