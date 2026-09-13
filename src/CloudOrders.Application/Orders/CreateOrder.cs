using CloudOrders.Application.Messaging;
using CloudOrders.Application.Observability;
using CloudOrders.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace CloudOrders.Application.Orders;

public sealed record CreateOrderCommand(string? CustomerId);

public sealed record CreatedOrder(
    Guid Id,
    string CustomerId,
    OrderStatus Status,
    DateTimeOffset CreatedAtUtc);

public interface IOrderStore
{
    Task AddWithOutboxAsync(
        Order order,
        OrderCreated orderCreated,
        MessageMetadata metadata,
        OutboxTraceContext? traceContext,
        CancellationToken cancellationToken = default);

    Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed partial class CreateOrderHandler(
    IOrderStore orderStore,
    TimeProvider timeProvider,
    ILogger<CreateOrderHandler> logger)
{
    public async Task<CreatedOrder> HandleAsync(
        CreateOrderCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var order = Order.Create(
            Guid.NewGuid(),
            command.CustomerId,
            timeProvider.GetUtcNow());
        var orderCreated = new OrderCreated(
            order.Id,
            order.CustomerId,
            MapStatus(order.Status),
            order.CreatedAtUtc);
        var metadata = new MessageMetadata(
            Guid.NewGuid().ToString("D"),
            order.Id.ToString("D"),
            OrderCreated.Subject,
            OrderCreated.JsonContentType);

        using var activity = CloudOrdersTelemetry.StartOrderCreation(
            order.Id,
            metadata.MessageId,
            metadata.CorrelationId);
        using var scope = CloudOrdersTelemetry.BeginOrderScope(
            logger,
            CloudOrdersTelemetry.ApiServiceName,
            order.Id,
            metadata.MessageId,
            metadata.CorrelationId);
        var traceContext = CloudOrdersTelemetry.CaptureTraceContext(activity);
        using var persistenceActivity = CloudOrdersTelemetry.StartOutboxPersistence(
            order.Id,
            metadata.MessageId,
            metadata.CorrelationId);

        try
        {
            await orderStore.AddWithOutboxAsync(
                    order,
                    orderCreated,
                    metadata,
                    traceContext,
                    cancellationToken)
                .ConfigureAwait(false);
            CloudOrdersTelemetry.RecordOrderPersisted();
            CloudOrdersTelemetry.RecordOutboxPersisted();
            CloudOrdersTelemetry.SetOutcome(persistenceActivity, "enqueued");
            CloudOrdersTelemetry.SetOutcome(activity, "enqueued");
            LogOrderAndOutboxPersisted();
        }
        catch (Exception exception)
        {
            CloudOrdersTelemetry.SetFailure(persistenceActivity, exception);
            CloudOrdersTelemetry.SetFailure(activity, exception);
            LogOrderCreationFailed(exception, exception.GetType().Name);
            throw;
        }

        return new CreatedOrder(
            order.Id,
            order.CustomerId,
            order.Status,
            order.CreatedAtUtc);
    }

    private static string MapStatus(OrderStatus status)
    {
        return status switch
        {
            OrderStatus.Pending => OrderCreated.PendingStatus,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported order status.")
        };
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Persisted order and queued OrderCreated in the transactional outbox.")]
    private partial void LogOrderAndOutboxPersisted();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Order creation failed with {FailureType}.")]
    private partial void LogOrderCreationFailed(Exception exception, string failureType);
}

public sealed class GetOrderHandler(IOrderStore orderStore)
{
    public Task<Order?> HandleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return orderStore.GetByIdAsync(id, cancellationToken);
    }
}
