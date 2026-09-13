using System.Diagnostics;
using CloudOrders.Application.Messaging;
using CloudOrders.Application.Observability;
using CloudOrders.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrders.UnitTests;

public sealed class OutboxDispatcherTests
{
    [Fact]
    public async Task SuccessfulDispatchPublishesEnvelopeThenMarksMessagePublished()
    {
        var store = new FakeOutboxMessageStore(CreatePendingMessage());
        var publisher = new CapturingPublisher();
        await using var provider = BuildProvider(store);
        var dispatcher = CreateDispatcher(provider, publisher);

        await dispatcher.DispatchBatchAsync();

        var published = Assert.Single(publisher.Messages);
        Assert.Equal("message-123", published.MessageId);
        Assert.Equal("order-123", published.CorrelationId);
        Assert.Equal("CloudOrders.Orders.OrderCreated", published.Subject);
        Assert.Equal("application/json", published.ContentType);
        Assert.Equal("{\"orderId\":\"order-123\"}"u8.ToArray(), published.Payload.ToArray());
        Assert.Equal(1, store.MarkPublishedCalls);
        Assert.True(store.IsPublished);
        Assert.Empty(store.FailureTypes);
    }

    [Fact]
    public async Task FailedDispatchKeepsMessagePendingForRetry()
    {
        var store = new FakeOutboxMessageStore(CreatePendingMessage());
        var publisher = new CapturingPublisher();
        publisher.Exceptions.Enqueue(new InvalidOperationException("Service Bus unavailable."));
        await using var provider = BuildProvider(store);
        var dispatcher = CreateDispatcher(provider, publisher);

        await dispatcher.DispatchBatchAsync();

        Assert.False(store.IsPublished);
        Assert.Equal(0, store.MarkPublishedCalls);
        Assert.Equal([nameof(InvalidOperationException)], store.FailureTypes);
        Assert.Single(publisher.Messages);
    }

    [Fact]
    public async Task RetryReusesThePersistedMessageIdAndNativeMetadata()
    {
        var store = new FakeOutboxMessageStore(CreatePendingMessage());
        var publisher = new CapturingPublisher();
        publisher.Exceptions.Enqueue(new InvalidOperationException("Temporary send failure."));
        await using var provider = BuildProvider(store);
        var dispatcher = CreateDispatcher(provider, publisher);

        await dispatcher.DispatchBatchAsync();
        await dispatcher.DispatchBatchAsync();

        Assert.Equal(2, publisher.Messages.Count);
        Assert.All(publisher.Messages, message =>
        {
            Assert.Equal("message-123", message.MessageId);
            Assert.Equal("order-123", message.CorrelationId);
            Assert.Equal("CloudOrders.Orders.OrderCreated", message.Subject);
            Assert.Equal("application/json", message.ContentType);
        });
        Assert.True(store.IsPublished);
        Assert.Equal(1, store.MarkPublishedCalls);
    }

    [Fact]
    public async Task MarkPublishedFailureAllowsSafeDuplicatePublicationWithSameMessageId()
    {
        var store = new FakeOutboxMessageStore(CreatePendingMessage());
        store.MarkPublishedExceptions.Enqueue(new InvalidOperationException("Database update failed."));
        var publisher = new CapturingPublisher();
        await using var provider = BuildProvider(store);
        var dispatcher = CreateDispatcher(provider, publisher);

        await dispatcher.DispatchBatchAsync();
        await dispatcher.DispatchBatchAsync();

        Assert.Equal(2, publisher.Messages.Count);
        Assert.All(publisher.Messages, message => Assert.Equal("message-123", message.MessageId));
        Assert.Equal(2, store.MarkPublishedCalls);
        Assert.True(store.IsPublished);
    }

    [Fact]
    public async Task DispatchUsesPersistedW3cTraceContextAsItsParent()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CloudOrdersTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var store = new FakeOutboxMessageStore(CreatePendingMessage());
        await using var provider = BuildProvider(store);
        var dispatcher = CreateDispatcher(provider, new CapturingPublisher());

        await dispatcher.DispatchBatchAsync();

        var dispatch = Assert.Single(activities, activity => activity.OperationName == "cloudorders.outbox.dispatch");
        Assert.Equal("0123456789abcdef0123456789abcdef", dispatch.TraceId.ToString());
        Assert.Equal("0123456789abcdef", dispatch.ParentSpanId.ToString());
    }

    [Theory]
    [InlineData("Outbox:BatchSize", "0")]
    [InlineData("Outbox:PollingIntervalSeconds", "0")]
    [InlineData("Outbox:Enabled", "not-a-boolean")]
    public void InvalidOptionsAreRejected(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        Assert.Throws<InvalidOperationException>(() => OutboxDispatcherOptions.FromConfiguration(configuration));
    }

    private static ServiceProvider BuildProvider(FakeOutboxMessageStore store)
    {
        return new ServiceCollection()
            .AddScoped<IOutboxMessageStore>(_ => store)
            .BuildServiceProvider();
    }

    private static OutboxDispatcherHostedService CreateDispatcher(
        IServiceProvider provider,
        CapturingPublisher publisher)
    {
        return new OutboxDispatcherHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            publisher,
            new OutboxDispatcherOptions(true, 20, TimeSpan.FromSeconds(5)),
            TimeProvider.System,
            NullLogger<OutboxDispatcherHostedService>.Instance);
    }

    private static PendingOutboxMessage CreatePendingMessage()
    {
        return new PendingOutboxMessage(
            Guid.Parse("00000000-0000-0000-0000-000000000123"),
            Guid.Parse("00000000-0000-0000-0000-000000000456"),
            new OutboundMessage(
                "message-123",
                "order-123",
                "CloudOrders.Orders.OrderCreated",
                "application/json",
                "{\"orderId\":\"order-123\"}"u8.ToArray()),
            new OutboxTraceContext("00-0123456789abcdef0123456789abcdef-0123456789abcdef-01", null));
    }

    private sealed class FakeOutboxMessageStore(PendingOutboxMessage pending) : IOutboxMessageStore
    {
        public bool IsPublished { get; private set; }

        public int MarkPublishedCalls { get; private set; }

        public Queue<Exception> MarkPublishedExceptions { get; } = new();

        public List<string> FailureTypes { get; } = [];

        public Task<IReadOnlyList<PendingOutboxMessage>> GetPendingAsync(
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<PendingOutboxMessage> messages = IsPublished ? [] : [pending];
            return Task.FromResult(messages);
        }

        public Task MarkPublishedAsync(
            Guid id,
            DateTimeOffset attemptedAtUtc,
            CancellationToken cancellationToken = default)
        {
            MarkPublishedCalls++;

            if (MarkPublishedExceptions.TryDequeue(out var exception))
            {
                return Task.FromException(exception);
            }

            IsPublished = true;
            return Task.CompletedTask;
        }

        public Task RecordFailedAttemptAsync(
            Guid id,
            DateTimeOffset attemptedAtUtc,
            string failureType,
            CancellationToken cancellationToken = default)
        {
            FailureTypes.Add(failureType);
            return Task.CompletedTask;
        }

        public Task<long> CountPendingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(IsPublished ? 0L : 1L);
        }
    }

    private sealed class CapturingPublisher : IMessagePublisher
    {
        public List<OutboundMessage> Messages { get; } = [];

        public Queue<Exception> Exceptions { get; } = new();

        public Task PublishAsync(OutboundMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);

            return Exceptions.TryDequeue(out var exception)
                ? Task.FromException(exception)
                : Task.CompletedTask;
        }

        public Task PublishAsync<TPayload>(
            TPayload payload,
            MessageMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Typed publishing is not used by the outbox dispatcher.");
        }
    }
}
