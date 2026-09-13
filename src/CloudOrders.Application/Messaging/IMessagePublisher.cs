namespace CloudOrders.Application.Messaging;

public sealed record MessageMetadata(
    string MessageId,
    string? CorrelationId,
    string Subject,
    string ContentType);

public interface IMessagePublisher
{
    Task PublishAsync(
        OutboundMessage message,
        CancellationToken cancellationToken = default);

    Task PublishAsync<TPayload>(
        TPayload payload,
        MessageMetadata metadata,
        CancellationToken cancellationToken = default);
}
