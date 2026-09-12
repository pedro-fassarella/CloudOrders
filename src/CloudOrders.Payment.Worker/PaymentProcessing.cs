using CloudOrders.Application.Messaging;

namespace CloudOrders.Payment.Worker;

internal sealed record SimulatedPaymentReceipt(Guid OrderId, string PaymentReference);

internal interface IPaymentProcessor
{
    Task<SimulatedPaymentReceipt> ProcessAsync(
        OrderCreated orderCreated,
        CancellationToken cancellationToken = default);
}

internal sealed class SimulatedPaymentProcessor : IPaymentProcessor
{
    public Task<SimulatedPaymentReceipt> ProcessAsync(
        OrderCreated orderCreated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderCreated);

        if (!string.Equals(orderCreated.Status, OrderCreated.PendingStatus, StringComparison.Ordinal))
        {
            throw new UnsupportedPaymentOrderStatusException(orderCreated.Status);
        }

        var receipt = new SimulatedPaymentReceipt(
            orderCreated.OrderId,
            $"simulated-payment-{orderCreated.OrderId:N}");

        return Task.FromResult(receipt);
    }
}

internal sealed class UnsupportedPaymentOrderStatusException(string status)
    : PermanentMessageFailureException(
        PermanentMessageFailureKind.UnsupportedBusinessStatus,
        $"Order status '{status}' is not supported for simulated payment processing.")
{
    public string Status { get; } = status;
}
