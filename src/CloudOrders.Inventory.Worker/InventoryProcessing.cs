using CloudOrders.Application.Messaging;

namespace CloudOrders.Inventory.Worker;

internal sealed record SimulatedInventoryReservation(Guid OrderId, string ReservationReference);

internal interface IInventoryProcessor
{
    Task<SimulatedInventoryReservation> ProcessAsync(
        OrderCreated orderCreated,
        CancellationToken cancellationToken = default);
}

internal sealed class SimulatedInventoryProcessor : IInventoryProcessor
{
    public Task<SimulatedInventoryReservation> ProcessAsync(
        OrderCreated orderCreated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderCreated);

        if (!string.Equals(orderCreated.Status, OrderCreated.PendingStatus, StringComparison.Ordinal))
        {
            throw new UnsupportedInventoryOrderStatusException(orderCreated.Status);
        }

        var reservation = new SimulatedInventoryReservation(
            orderCreated.OrderId,
            $"simulated-reservation-{orderCreated.OrderId:N}");

        return Task.FromResult(reservation);
    }
}

internal sealed class UnsupportedInventoryOrderStatusException(string status)
    : PermanentMessageFailureException(
        PermanentMessageFailureKind.UnsupportedBusinessStatus,
        $"Order status '{status}' is not supported for simulated inventory processing.")
{
    public string Status { get; } = status;
}
