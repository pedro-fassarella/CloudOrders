using System.Text.Json;
using CloudOrders.Application.Messaging;
using CloudOrders.Notification.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrders.UnitTests;

public sealed class NotificationMessageProcessorTests
{
    [Fact]
    public async Task ProcessAsyncDeserializesTheContractBeforeNotificationProcessing()
    {
        var expected = CreateOrderCreated("AwaitingManualReview");
        var notificationProcessor = new CapturingNotificationProcessor();
        var messageProcessor = CreateMessageProcessor(notificationProcessor);

        await messageProcessor.ProcessAsync(
            Serialize(expected),
            "message-123",
            expected.OrderId.ToString("D"),
            OrderCreated.Subject,
            OrderCreated.JsonContentType,
            _ => Task.CompletedTask);

        Assert.Equal(expected, notificationProcessor.OrderCreated);
    }

    [Fact]
    public async Task ProcessAsyncProcessesThenCompletesAValidPendingOrderExactlyOnce()
    {
        var calls = new List<string>();
        var expected = CreateOrderCreated(OrderCreated.PendingStatus);
        var notificationProcessor = new CapturingNotificationProcessor(calls);
        var messageProcessor = CreateMessageProcessor(notificationProcessor);
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

        Assert.Equal(expected, notificationProcessor.OrderCreated);
        Assert.Equal(["notification", "complete"], calls);
        Assert.Equal(1, completions);
    }

    [Fact]
    public async Task ProcessAsyncSkipsDuplicateNotificationButCompletesBothDeliveries()
    {
        var orderCreated = CreateOrderCreated(OrderCreated.PendingStatus);
        var notificationProcessor = new CapturingNotificationProcessor();
        var processedMessages = new InMemoryProcessedMessageStore();
        var messageProcessor = CreateMessageProcessor(notificationProcessor, processedMessages);
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

        Assert.Equal(1, notificationProcessor.CallCount);
        Assert.Equal(2, completions);
        Assert.Equal([ConsumerIdentities.Notification, ConsumerIdentities.Notification], processedMessages.ConsumerNames);
    }

    [Fact]
    public async Task ProcessAsyncDeadLettersAnInvalidNotificationContract()
    {
        var notificationProcessor = new CapturingNotificationProcessor();
        var messageProcessor = CreateMessageProcessor(notificationProcessor);
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

        Assert.Null(notificationProcessor.OrderCreated);
        Assert.Equal(0, completions);
        Assert.NotNull(deadLetter);
        Assert.Equal(MessageDeadLetterDetailsFactory.MessageContractViolationReason, deadLetter.Reason);
        Assert.Contains("Notification", deadLetter.Description);
        Assert.Contains("message-123", deadLetter.Description);
    }

    [Fact]
    public async Task ProcessAsyncDoesNotCompleteWhenTheContentTypeIsInvalid()
    {
        var notificationProcessor = new CapturingNotificationProcessor();
        var messageProcessor = CreateMessageProcessor(notificationProcessor);
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

        Assert.Null(notificationProcessor.OrderCreated);
        Assert.Equal(0, completions);
    }

    [Fact]
    public async Task ProcessAsyncDoesNotCompleteWhenTheBodyIsEmpty()
    {
        var notificationProcessor = new CapturingNotificationProcessor();
        var messageProcessor = CreateMessageProcessor(notificationProcessor);
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

        Assert.Null(notificationProcessor.OrderCreated);
        Assert.Equal(0, completions);
    }

    [Fact]
    public async Task ProcessAsyncDoesNotCompleteWhenJsonCannotBeDeserialized()
    {
        var notificationProcessor = new CapturingNotificationProcessor();
        var messageProcessor = CreateMessageProcessor(notificationProcessor);
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

        Assert.Null(notificationProcessor.OrderCreated);
        Assert.Equal(0, completions);
    }

    [Fact]
    public async Task ProcessAsyncDeadLettersUnsupportedNotificationStatusWithoutCompletedInboxState()
    {
        var orderCreated = CreateOrderCreated("Cancelled");
        var processedMessages = new InMemoryProcessedMessageStore();
        var messageProcessor = CreateMessageProcessor(new SimulatedNotificationProcessor(), processedMessages);
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
        Assert.False(processedMessages.IsCompleted(ConsumerIdentities.Notification, "message-123"));
    }

    [Fact]
    public async Task ProcessAsyncDoesNotCompleteWhenNotificationProcessingFails()
    {
        var processedMessages = new InMemoryProcessedMessageStore();
        var messageProcessor = CreateMessageProcessor(new FailingNotificationProcessor(), processedMessages);
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
        Assert.False(processedMessages.IsCompleted(ConsumerIdentities.Notification, "message-123"));
    }

    [Fact]
    public async Task ProcessAsyncLeavesTransientNotificationFailureUnsettledAndRetryable()
    {
        var orderCreated = CreateOrderCreated(OrderCreated.PendingStatus);
        var notificationProcessor = new FailingOnceNotificationProcessor();
        var processedMessages = new InMemoryProcessedMessageStore();
        var messageProcessor = CreateMessageProcessor(notificationProcessor, processedMessages);
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
        Assert.False(processedMessages.IsCompleted(ConsumerIdentities.Notification, "message-transient"));

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

        Assert.Equal(2, notificationProcessor.CallCount);
        Assert.Equal(1, completions);
        Assert.Equal(0, deadLetters);
        Assert.True(processedMessages.IsCompleted(ConsumerIdentities.Notification, "message-transient"));
    }

    [Fact]
    public async Task ProcessAsyncDoesNotReexecuteNotificationWhenCompletionFailsAfterProcessing()
    {
        var orderCreated = CreateOrderCreated(OrderCreated.PendingStatus);
        var notificationProcessor = new CapturingNotificationProcessor();
        var processedMessages = new InMemoryProcessedMessageStore();
        var messageProcessor = CreateMessageProcessor(notificationProcessor, processedMessages);
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

        Assert.Equal(1, notificationProcessor.CallCount);
        Assert.Equal(2, completionAttempts);
    }

    [Fact]
    public async Task SimulatedNotificationProcessorReturnsAStableReceiptForPendingOrders()
    {
        var orderCreated = CreateOrderCreated(OrderCreated.PendingStatus);
        var notificationProcessor = new SimulatedNotificationProcessor();

        var first = await notificationProcessor.ProcessAsync(orderCreated);
        var second = await notificationProcessor.ProcessAsync(orderCreated);

        Assert.Equal(orderCreated.OrderId, first.OrderId);
        Assert.Equal("simulated", first.NotificationChannel);
        Assert.Equal($"simulated-notification-{orderCreated.OrderId:N}", first.NotificationReference);
        Assert.Equal(first, second);
    }

    private static NotificationMessageProcessor CreateMessageProcessor(
        INotificationProcessor notificationProcessor,
        IProcessedMessageStore? processedMessageStore = null)
    {
        return new NotificationMessageProcessor(
            notificationProcessor,
            processedMessageStore ?? new InMemoryProcessedMessageStore(),
            NullLogger<NotificationMessageProcessor>.Instance);
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

    private sealed class CapturingNotificationProcessor(List<string>? calls = null) : INotificationProcessor
    {
        public OrderCreated? OrderCreated { get; private set; }

        public int CallCount { get; private set; }

        public Task<SimulatedNotificationReceipt> ProcessAsync(
            OrderCreated orderCreated,
            CancellationToken cancellationToken = default)
        {
            OrderCreated = orderCreated;
            CallCount++;
            calls?.Add("notification");

            return Task.FromResult(new SimulatedNotificationReceipt(
                orderCreated.OrderId,
                "captured",
                "captured-notification"));
        }
    }

    private sealed class FailingNotificationProcessor : INotificationProcessor
    {
        public Task<SimulatedNotificationReceipt> ProcessAsync(
            OrderCreated orderCreated,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<SimulatedNotificationReceipt>(
                new InvalidOperationException("Simulated notification processing failure."));
        }
    }

    private sealed class FailingOnceNotificationProcessor : INotificationProcessor
    {
        public int CallCount { get; private set; }

        public Task<SimulatedNotificationReceipt> ProcessAsync(
            OrderCreated orderCreated,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            return CallCount == 1
                ? Task.FromException<SimulatedNotificationReceipt>(new InvalidOperationException("Simulated transient notification failure."))
                : Task.FromResult(new SimulatedNotificationReceipt(
                    orderCreated.OrderId,
                    "retried",
                    "retried-notification"));
        }
    }
}
