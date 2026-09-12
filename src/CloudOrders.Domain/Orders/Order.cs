namespace CloudOrders.Domain.Orders;

public enum OrderStatus
{
    Pending = 0
}

public sealed class Order
{
    private Order()
    {
    }

    private Order(Guid id, string customerId, DateTimeOffset createdAtUtc)
    {
        Id = id;
        CustomerId = customerId;
        CreatedAtUtc = createdAtUtc;
        Status = OrderStatus.Pending;
    }

    public Guid Id { get; private set; }

    public string CustomerId { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public OrderStatus Status { get; private set; }

    public static Order Create(Guid id, string? customerId, DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Order id must not be empty.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(customerId))
        {
            throw new ArgumentException("Customer id is required.", nameof(customerId));
        }

        return new Order(id, customerId.Trim(), createdAtUtc);
    }
}
