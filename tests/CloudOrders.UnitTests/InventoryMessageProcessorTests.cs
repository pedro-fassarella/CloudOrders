using System.Text.Json;
using CloudOrders.Application.Messaging;
using CloudOrders.Inventory.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrders.UnitTests;

public sealed class InventoryMessageProcessorTests
{
    [Fact]
    public async Task ProcessAsyncDeserializesTheContractBeforeInventoryProcessing()
    {
        var expected = CreateOrderCreated("AwaitingManualReview");
        var inventoryProcessor = new CapturingInventoryProcessor();
        var messageProcessor = CreateMessageProcessor(inventoryProcessor);

        await messageProcessor.ProcessAsync(
            Serialize(expected),
            "message-123",
            expected.OrderId.ToString("D"),
            OrderCreated.Subject,
            OrderCreated.JsonContentType,
            _ => Task.CompletedTask);

        Assert.Equal(expected, inventoryProcessor.OrderCreated);
    }

    [Fact]
    public async Task ProcessAsyncProcessesThenCompletesAValidPendingOrderExactlyOnce()
    {
        var calls = new List<string>();
        var expected = CreateOrderCreated(OrderCreated.PendingStatus);
        var inventoryProcessor = new CapturingInventoryProcessor(calls);
        var messageProcessor = CreateMessageProcessor(inventoryProcessor);
        var completions = 0;

        await messageProcessor.ProcessAsync(
            Serialize(expected),
            "message-123",
            expected.OrderId.ToString("D"),
            OrderCreated.Subject,
            OrderCreated.JsonContentType,
            _ =>
            {
                calls.Add("complete");
                completions++;
                return Task.CompletedTask;
            });

        Assert.Equal(expected, inventoryProcessor.OrderCreated);
        Assert.Equal(["inventory", "complete"], calls);
        Assert.Equal(1, completions);
    }

    [Fact]
    public async Task ProcessAsyncSkipsDuplicateInventoryButCompletesBothDeliveries()
    {
        var orderCreated = CreateOrderCreated(OrderCreated.PendingStatus);
        var inventoryProcessor = new CapturingInventoryProcessor();
        var processedMessages = new InMemoryProcessedMessageStore();
        var messageProcessor = CreateMessageProcessor(inventoryProcessor, processedMessages);
        var completions = 0;

        for (var delivery = 0; delivery < 2; delivery++)
        {
            await messageProcessor.ProcessAsync(
                Serialize(orderCreated),
                "message-duplicate",
                orderCreated.OrderId.ToString("D"),
                OrderCreated.Subject,
                OrderCreated.JsonContentType,
                _ =>
                {
                    completions++;
                    return Task.CompletedTask;
                });
        }

        Assert.Equal(1, inventoryProcessor.CallCount);
        Assert.Equal(2, completions);
        Assert.Equal([ConsumerIdentities.Inventory, ConsumerIdentities.Inventory], processedMessages.ConsumerNames);
    }

    [Fact]
    public async Task ProcessAsyncDeadLettersAnInvalidInventoryContract()
    {
        var inventoryProcessor = new CapturingInventoryProcessor();
        var messageProcessor = CreateMessageProcessor(inventoryProcessor);
        var completions = 0;
        MessageDeadLetterDetails? deadLetter = null;

        await messageProcessor.ProcessAsync(
            Serialize(CreateOrderCreated(OrderCreated.PendingStatus)),
            "message-123",
            "correlation-456",
            "CloudOrders.Orders.Unexpected",
            OrderCreated.JsonContentType,
            _ =>
            {
                completions++;
                return Task.CompletedTask;
            },
            (details, _) =>
            {
                deadLetter = details;
                return Task.CompletedTask;
            });

        Assert.Null(inventoryProcessor.OrderCreated);
        Assert.Equal(0, completions);
        Assert.NotNull(deadLetter);
        Assert.Equal(MessageDeadLetterDetailsFactory.MessageContractViolationReason, deadLetter.Reason);
        Assert.Contains("Inventory", deadLetter.Description);
        Assert.Contains("message-123", deadLetter.Description);
    }

    [Fact]
    public async Task ProcessAsyncDoesNotCompleteWhenTheContentTypeIsInvalid()
    {
        var inventoryProcessor = new CapturingInventoryProcessor();
        var messageProcessor = CreateMessageProcessor(inventoryProcessor);
        var completions = 0;

        await Assert.ThrowsAsync<PermanentMessageFailureException>(
            () => messageProcessor.ProcessAsync(
                Serialize(CreateOrderCreated(OrderCreated.PendingStatus)),
                "message-123",
                "correlation-456",
                OrderCreated.Subject,
                "text/plain",
                _ =>
                {
                    completions++;
                    return Task.CompletedTask;
                }));

        Assert.Null(inventoryProcessor.OrderCreated);
        Assert.Equal(0, completions);
    }

    [Fact]
    public async Task ProcessAsyncDoesNotCompleteWhenTheBodyIsEmpty()
    {
        var inventoryProcessor = new CapturingInventoryProcessor();
        var messageProcessor = CreateMessageProcessor(inventoryProcessor);
        var completions = 0;

        await Assert.ThrowsAsync<PermanentMessageFailureException>(
            () => messageProcessor.ProcessAsync(
                ReadOnlyMemory<byte>.Empty,
                "message-123",
                "correlation-456",
                OrderCreated.Subject,
                OrderCreated.JsonContentType,
                _ =>
                {
                    completions++;
                    return Task.CompletedTask;
                }));

        Assert.Null(inventoryProcessor.OrderCreated);
        Assert.Equal(0, completions);
    }

    [Fact]
    public async Task ProcessAsyncDoesNotCompleteWhenJsonCannotBeDeserialized()
    {
        var inventoryProcessor = new CapturingInventoryProcessor();
        var messageProcessor = CreateMessageProcessor(inventoryProcessor);
        var completions = 0;

        await Assert.ThrowsAsync<PermanentMessageFailureException>(
            () => messageProcessor.ProcessAsync(
                "not-json"u8.ToArray(),
                "message-123",
                "correlation-456",
                OrderCreated.Subject,
                OrderCreated.JsonContentType,
                _ =>
                {
                    completions++;
                    return Task.CompletedTask;
                }));

        Assert.Null(inventoryProcessor.OrderCreated);
        Assert.Equal(0, completions);
    }

    [Fact]
    public async Task ProcessAsyncDeadLettersUnsupportedInventoryStatusWithoutCompletedInboxState()
    {
        var orderCreated = CreateOrderCreated("Cancelled");
        var processedMessages = new InMemoryProcessedMessageStore();
        var messageProcessor = CreateMessageProcessor(new SimulatedInventoryProcessor(), processedMessages);
        var completions = 0;
        MessageDeadLetterDetails? deadLetter = null;

        await messageProcessor.ProcessAsync(
            Serialize(orderCreated),
            "message-123",
            orderCreated.OrderId.ToString("D"),
            OrderCreated.Subject,
            OrderCreated.JsonContentType,
            _ =>
            {
                completions++;
                return Task.CompletedTask;
            },
            (details, _) =>
            {
                deadLetter = details;
                return Task.CompletedTask;
            });

        Assert.Equal(0, completions);
        Assert.NotNull(deadLetter);
        Assert.Equal(MessageDeadLetterDetailsFactory.UnsupportedBusinessStatusReason, deadLetter.Reason);
        Assert.Contains("Cancelled", deadLetter.Description);
        Assert.False(processedMessages.IsCompleted(ConsumerIdentities.Inventory, "message-123"));
    }

    [Fact]
    public async Task ProcessAsyncDoesNotCompleteWhenInventoryProcessingFails()
    {
        var processedMessages = new InMemoryProcessedMessageStore();
        var messageProcessor = CreateMessageProcessor(new FailingInventoryProcessor(), processedMessages);
        var completions = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => messageProcessor.ProcessAsync(
                Serialize(CreateOrderCreated(OrderCreated.PendingStatus)),
                "message-123",
                "correlation-456",
                OrderCreated.Subject,
                OrderCreated.JsonContentType,
                _ =>
                {
                    completions++;
                    return Task.CompletedTask;
                }));

        Assert.Equal(0, completions);
        Assert.False(processedMessages.IsCompleted(ConsumerIdentities.Inventory, "message-123"));
    }

    [Fact]
    public async Task ProcessAsyncLeavesTransientInventoryFailureUnsettledAndRetryable()
    {
        var orderCreated = CreateOrderCreated(OrderCreated.PendingStatus);
        var inventoryProcessor = new FailingOnceInventoryProcessor();
        var processedMessages = new InMemoryProcessedMessageStore();
        var messageProcessor = CreateMessageProcessor(inventoryProcessor, processedMessages);
        var completions = 0;
        var deadLetters = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => messageProcessor.ProcessAsync(
                Serialize(orderCreated),
                "message-transient",
                orderCreated.OrderId.ToString("D"),
                OrderCreated.Subject,
                OrderCreated.JsonContentType,
                _ =>
                {
                    completions++;
                    return Task.CompletedTask;
                },
                (_, _) =>
                {
                    deadLetters++;
                    return Task.CompletedTask;
                }));

        Assert.Equal(0, completions);
        Assert.Equal(0, deadLetters);
        Assert.False(processedMessages.IsCompleted(ConsumerIdentities.Inventory, "message-transient"));

        await messageProcessor.ProcessAsync(
            Serialize(orderCreated),
            "message-transient",
            orderCreated.OrderId.ToString("D"),
            OrderCreated.Subject,
            OrderCreated.JsonContentType,
            _ =>
            {
                completions++;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                deadLetters++;
                return Task.CompletedTask;
            });

        Assert.Equal(2, inventoryProcessor.CallCount);
        Assert.Equal(1, completions);
        Assert.Equal(0, deadLetters);
        Assert.True(processedMessages.IsCompleted(ConsumerIdentities.Inventory, "message-transient"));
    }

    [Fact]
    public async Task ProcessAsyncDoesNotReexecuteInventoryWhenCompletionFailsAfterProcessing()
    {
        var orderCreated = CreateOrderCreated(OrderCreated.PendingStatus);
        var inventoryProcessor = new CapturingInventoryProcessor();
        var processedMessages = new InMemoryProcessedMessageStore();
        var messageProcessor = CreateMessageProcessor(inventoryProcessor, processedMessages);
        var completionAttempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => messageProcessor.ProcessAsync(
                Serialize(orderCreated),
                "message-completion-failure",
                orderCreated.OrderId.ToString("D"),
                OrderCreated.Subject,
                OrderCreated.JsonContentType,
                _ =>
                {
                    completionAttempts++;
                    return Task.FromException(new InvalidOperationException("Settlement failed."));
                },
                (_, _) => Task.CompletedTask));

        await messageProcessor.ProcessAsync(
            Serialize(orderCreated),
            "message-completion-failure",
            orderCreated.OrderId.ToString("D"),
            OrderCreated.Subject,
            OrderCreated.JsonContentType,
            _ =>
            {
                completionAttempts++;
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask);

        Assert.Equal(1, inventoryProcessor.CallCount);
        Assert.Equal(2, completionAttempts);
    }

    [Fact]
    public async Task SimulatedInventoryProcessorReturnsAStableReferenceForPendingOrders()
    {
        var orderCreated = CreateOrderCreated(OrderCreated.PendingStatus);
        var inventoryProcessor = new SimulatedInventoryProcessor();

        var first = await inventoryProcessor.ProcessAsync(orderCreated);
        var second = await inventoryProcessor.ProcessAsync(orderCreated);

        Assert.Equal(orderCreated.OrderId, first.OrderId);
        Assert.Equal($"simulated-reservation-{orderCreated.OrderId:N}", first.ReservationReference);
        Assert.Equal(first, second);
    }

    private static InventoryMessageProcessor CreateMessageProcessor(
        IInventoryProcessor inventoryProcessor,
        IProcessedMessageStore? processedMessageStore = null)
    {
        return new InventoryMessageProcessor(
            inventoryProcessor,
            processedMessageStore ?? new InMemoryProcessedMessageStore(),
            NullLogger<InventoryMessageProcessor>.Instance);
    }

    private static byte[] Serialize(OrderCreated orderCreated)
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            orderCreated,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static OrderCreated CreateOrderCreated(string status)
    {
        return new OrderCreated(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "customer-123",
            status,
            new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
    }

    private sealed class CapturingInventoryProcessor(List<string>? calls = null) : IInventoryProcessor
    {
        public OrderCreated? OrderCreated { get; private set; }

        public int CallCount { get; private set; }

        public Task<SimulatedInventoryReservation> ProcessAsync(
            OrderCreated orderCreated,
            CancellationToken cancellationToken = default)
        {
            OrderCreated = orderCreated;
            CallCount++;
            calls?.Add("inventory");

            return Task.FromResult(new SimulatedInventoryReservation(
                orderCreated.OrderId,
                "captured-reservation"));
        }
    }

    private sealed class FailingInventoryProcessor : IInventoryProcessor
    {
        public Task<SimulatedInventoryReservation> ProcessAsync(
            OrderCreated orderCreated,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<SimulatedInventoryReservation>(
                new InvalidOperationException("Simulated inventory processing failure."));
        }
    }

    private sealed class FailingOnceInventoryProcessor : IInventoryProcessor
    {
        public int CallCount { get; private set; }

        public Task<SimulatedInventoryReservation> ProcessAsync(
            OrderCreated orderCreated,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            return CallCount == 1
                ? Task.FromException<SimulatedInventoryReservation>(new InvalidOperationException("Simulated transient inventory failure."))
                : Task.FromResult(new SimulatedInventoryReservation(orderCreated.OrderId, "retried-reservation"));
        }
    }
}
