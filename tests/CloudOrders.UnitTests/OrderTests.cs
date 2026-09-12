using CloudOrders.Domain.Orders;

namespace CloudOrders.UnitTests;

public sealed class OrderTests
{
    [Fact]
    public void CreateSetsIdentityTimestampAndPendingStatus()
    {
        var id = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

        var order = Order.Create(id, " customer-123 ", createdAt);

        Assert.Equal(id, order.Id);
        Assert.Equal("customer-123", order.CustomerId);
        Assert.Equal(createdAt, order.CreatedAtUtc);
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void CreateRejectsMissingCustomerId(string? customerId)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Order.Create(Guid.NewGuid(), customerId, DateTimeOffset.UtcNow));

        Assert.Equal("customerId", exception.ParamName);
    }
}
