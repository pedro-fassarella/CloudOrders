using CloudOrders.Application.Messaging;
using CloudOrders.Application.Orders;
using CloudOrders.Domain.Orders;

namespace CloudOrders.UnitTests;

public sealed class CreateOrderHandlerTests
{
    [Fact]
    public async Task HandleAsyncPersistsThenPublishesAnOrderCreatedEvent()
    {
        var calls = new List<string>();
        var store = new CapturingOrderStore(calls);
        var publisher = new CapturingMessagePublisher(calls);
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        var handler = new CreateOrderHandler(store, publisher, time);

        var result = await handler.HandleAsync(new CreateOrderCommand("customer-123"));

        Assert.NotNull(store.Order);
        Assert.Equal(store.Order!.Id, result.Id);
        Assert.Equal("customer-123", result.CustomerId);
        Assert.Equal(OrderStatus.Pending, result.Status);
        Assert.Equal(time.GetUtcNow(), result.CreatedAtUtc);
        Assert.Equal(["persist", "publish"], calls);

        var published = Assert.IsType<OrderCreated>(publisher.Payload);
        var metadata = Assert.IsType<MessageMetadata>(publisher.Metadata);
        Assert.Equal(result.Id, published.OrderId);
        Assert.Equal(result.CustomerId, published.CustomerId);
        Assert.Equal(OrderCreated.PendingStatus, published.Status);
        Assert.Equal(result.CreatedAtUtc, published.CreatedAtUtc);
        Assert.Equal(OrderCreated.Subject, metadata.Subject);
        Assert.Equal(OrderCreated.JsonContentType, metadata.ContentType);
        Assert.Equal(result.Id.ToString("D"), metadata.CorrelationId);
        Assert.True(Guid.TryParse(metadata.MessageId, out _));
    }

    [Fact]
    public async Task HandleAsyncDoesNotPublishWhenPersistenceFails()
    {
        var store = new CapturingOrderStore
        {
            AddException = new InvalidOperationException("Persistence failed.")
        };
        var publisher = new CapturingMessagePublisher();
        var handler = new CreateOrderHandler(store, publisher, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new CreateOrderCommand("customer-123")));

        Assert.Null(publisher.Payload);
        Assert.Null(publisher.Metadata);
    }

    [Fact]
    public async Task HandleAsyncPropagatesPublishFailureAfterPersistence()
    {
        var store = new CapturingOrderStore();
        var publisher = new CapturingMessagePublisher
        {
            PublishException = new InvalidOperationException("Publishing failed.")
        };
        var handler = new CreateOrderHandler(store, publisher, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new CreateOrderCommand("customer-123")));

        Assert.NotNull(store.Order);
        Assert.IsType<OrderCreated>(publisher.Payload);
    }

    [Fact]
    public async Task GetOrderReturnsTheStoreResult()
    {
        var order = Order.Create(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "customer-123",
            new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        var store = new CapturingOrderStore { ExistingOrder = order };
        var handler = new GetOrderHandler(store);

        var result = await handler.HandleAsync(order.Id);

        Assert.Same(order, result);
        Assert.Equal(order.Id, store.RequestedId);
    }

    private sealed class CapturingOrderStore(List<string>? calls = null) : IOrderStore
    {
        public Order? Order { get; private set; }

        public Order? ExistingOrder { get; init; }

        public Guid? RequestedId { get; private set; }

        public Exception? AddException { get; init; }

        public Task AddAsync(Order order, CancellationToken cancellationToken = default)
        {
            if (AddException is not null)
            {
                return Task.FromException(AddException);
            }

            Order = order;
            calls?.Add("persist");
            return Task.CompletedTask;
        }

        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            RequestedId = id;
            return Task.FromResult(ExistingOrder);
        }
    }

    private sealed class CapturingMessagePublisher(List<string>? calls = null) : IMessagePublisher
    {
        public object? Payload { get; private set; }

        public MessageMetadata? Metadata { get; private set; }

        public Exception? PublishException { get; init; }

        public Task PublishAsync<TPayload>(
            TPayload payload,
            MessageMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            Payload = payload;
            Metadata = metadata;
            calls?.Add("publish");

            return PublishException is null
                ? Task.CompletedTask
                : Task.FromException(PublishException);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
