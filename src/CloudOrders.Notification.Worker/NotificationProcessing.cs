using CloudOrders.Application.Messaging;

namespace CloudOrders.Notification.Worker;

internal sealed record SimulatedNotificationReceipt(
    Guid OrderId,
    string NotificationChannel,
    string NotificationReference);

internal interface INotificationProcessor
{
    Task<SimulatedNotificationReceipt> ProcessAsync(
        OrderCreated orderCreated,
        CancellationToken cancellationToken = default);
}

internal sealed class SimulatedNotificationProcessor : INotificationProcessor
{
    public Task<SimulatedNotificationReceipt> ProcessAsync(
        OrderCreated orderCreated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderCreated);

        if (!string.Equals(orderCreated.Status, OrderCreated.PendingStatus, StringComparison.Ordinal))
        {
            throw new UnsupportedNotificationOrderStatusException(orderCreated.Status);
        }

        var receipt = new SimulatedNotificationReceipt(
            orderCreated.OrderId,
            "simulated",
            $"simulated-notification-{orderCreated.OrderId:N}");

        return Task.FromResult(receipt);
    }
}

internal sealed class UnsupportedNotificationOrderStatusException(string status)
    : PermanentMessageFailureException(
        PermanentMessageFailureKind.UnsupportedBusinessStatus,
        $"Order status '{status}' is not supported for simulated notification processing.")
{
    public string Status { get; } = status;
}
