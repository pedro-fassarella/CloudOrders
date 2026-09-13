using System.Diagnostics;
using CloudOrders.Application.Messaging;
using CloudOrders.Application.Observability;
using CloudOrders.Application.Orders;
using CloudOrders.Domain.Orders;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrders.UnitTests;

public sealed class CreateOrderHandlerTests
{
    [Fact]
    public async Task HandleAsyncPersistsOrderAndOrderCreatedOutboxMessage()
    {
        var store = new CapturingOrderStore();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        var handler = new CreateOrderHandler(store, time, NullLogger<CreateOrderHandler>.Instance);

        var result = await handler.HandleAsync(new CreateOrderCommand("customer-123"));

        Assert.NotNull(store.Order);
        Assert.Equal(store.Order!.Id, result.Id);
        Assert.Equal("customer-123", result.CustomerId);
        Assert.Equal(OrderStatus.Pending, result.Status);
        Assert.Equal(time.GetUtcNow(), result.CreatedAtUtc);

        var orderCreated = Assert.IsType<OrderCreated>(store.OrderCreated);
        var metadata = Assert.IsType<MessageMetadata>(store.Metadata);
        Assert.Equal(result.Id, orderCreated.OrderId);
        Assert.Equal(result.CustomerId, orderCreated.CustomerId);
        Assert.Equal(OrderCreated.PendingStatus, orderCreated.Status);
        Assert.Equal(result.CreatedAtUtc, orderCreated.CreatedAtUtc);
        Assert.Equal(OrderCreated.Subject, metadata.Subject);
        Assert.Equal(OrderCreated.JsonContentType, metadata.ContentType);
        Assert.Equal(result.Id.ToString("D"), metadata.CorrelationId);
        Assert.True(Guid.TryParse(metadata.MessageId, out _));
    }

    [Fact]
    public async Task HandleAsyncDoesNotCompleteWhenAtomicPersistenceFails()
    {
        var store = new CapturingOrderStore
        {
            AddException = new InvalidOperationException("Persistence failed.")
        };
        var handler = new CreateOrderHandler(store, TimeProvider.System, NullLogger<CreateOrderHandler>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new CreateOrderCommand("customer-123")));

        Assert.Null(store.Order);
        Assert.Null(store.OrderCreated);
        Assert.Null(store.Metadata);
    }

    [Fact]
    public async Task HandleAsyncCreatesTraceAndPersistsW3cContextAsOutboxMetadata()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CloudOrdersTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var store = new CapturingOrderStore();
        var handler = new CreateOrderHandler(
            store,
            TimeProvider.System,
            NullLogger<CreateOrderHandler>.Instance);

        var created = await handler.HandleAsync(new CreateOrderCommand("customer-123"));
        var metadata = Assert.IsType<MessageMetadata>(store.Metadata);
        var orderActivity = Assert.Single(activities, activity => activity.OperationName == "cloudorders.order.create");

        Assert.Equal(created.Id.ToString("D"), orderActivity.GetTagItem("cloudorders.order.id"));
        Assert.Equal(metadata.MessageId, orderActivity.GetTagItem("messaging.message.id"));
        Assert.Equal(metadata.CorrelationId, orderActivity.GetTagItem("messaging.conversation.id"));
        Assert.NotNull(store.TraceContext);
        Assert.Equal(orderActivity.Id, store.TraceContext!.TraceParent);
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

    private sealed class CapturingOrderStore : IOrderStore
    {
        public Order? Order { get; private set; }

        public OrderCreated? OrderCreated { get; private set; }

        public MessageMetadata? Metadata { get; private set; }

        public OutboxTraceContext? TraceContext { get; private set; }

        public Order? ExistingOrder { get; init; }

        public Guid? RequestedId { get; private set; }

        public Exception? AddException { get; init; }

        public Task AddWithOutboxAsync(
            Order order,
            OrderCreated orderCreated,
            MessageMetadata metadata,
            OutboxTraceContext? traceContext,
            CancellationToken cancellationToken = default)
        {
            if (AddException is not null)
            {
                return Task.FromException(AddException);
            }

            Order = order;
            OrderCreated = orderCreated;
            Metadata = metadata;
            TraceContext = traceContext;
            return Task.CompletedTask;
        }

        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            RequestedId = id;
            return Task.FromResult(ExistingOrder);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
