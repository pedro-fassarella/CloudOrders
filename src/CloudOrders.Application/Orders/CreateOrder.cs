using CloudOrders.Application.Messaging;
using CloudOrders.Domain.Orders;

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

public sealed class CreateOrderHandler(
    IOrderStore orderStore,
    IMessagePublisher publisher,
    TimeProvider timeProvider)
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

        await orderStore.AddAsync(order, cancellationToken).ConfigureAwait(false);

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

        await publisher.PublishAsync(orderCreated, metadata, cancellationToken).ConfigureAwait(false);

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
}

public sealed class GetOrderHandler(IOrderStore orderStore)
{
    public Task<Order?> HandleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return orderStore.GetByIdAsync(id, cancellationToken);
    }
}
