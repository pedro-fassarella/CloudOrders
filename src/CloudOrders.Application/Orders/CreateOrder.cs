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
    Task AddAsync(Order order, CancellationToken cancellationToken = default);

    Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed partial class CreateOrderHandler(
    IOrderStore orderStore,
    IMessagePublisher publisher,
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

        try
        {
            await orderStore.AddAsync(order, cancellationToken).ConfigureAwait(false);
            CloudOrdersTelemetry.RecordOrderPersisted();
            LogOrderPersisted();

            await publisher.PublishAsync(orderCreated, metadata, cancellationToken).ConfigureAwait(false);
            CloudOrdersTelemetry.RecordOrderPublished();
            LogOrderPublished();
            CloudOrdersTelemetry.SetOutcome(activity, "published");
        }
        catch (Exception exception)
        {
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
        Message = "Persisted order before publishing OrderCreated.")]
    private partial void LogOrderPersisted();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Published OrderCreated after order persistence.")]
    private partial void LogOrderPublished();

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
