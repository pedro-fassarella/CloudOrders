namespace CloudOrders.Application.Messaging;

public sealed record OrderCreated(
    Guid OrderId,
    string CustomerId,
    string Status,
    DateTimeOffset CreatedAtUtc)
{
    public const string Subject = "CloudOrders.Orders.OrderCreated";

    public const string JsonContentType = "application/json";

    public const string PendingStatus = "Pending";
}
