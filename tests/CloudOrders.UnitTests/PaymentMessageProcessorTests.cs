using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using CloudOrders.Application.Messaging;
using CloudOrders.Application.Observability;
using CloudOrders.Payment.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrders.UnitTests;

public sealed class PaymentMessageProcessorTests
{
    [Fact]
    public async Task ProcessAsyncDeserializesTheContractBeforeBusinessStatusEvaluation()
    {
        var expected = CreateOrderCreated("AwaitingManualReview");
        var paymentProcessor = new CapturingPaymentProcessor();
        var messageProcessor = CreateMessageProcessor(paymentProcessor);
        var completions = 0;

        await messageProcessor.ProcessAsync(
            Serialize(expected),
            "message-123",
            expected.OrderId.ToString("D"),
            OrderCreated.Subject,
            OrderCreated.JsonContentType,
            _ =>
            {
                completions++;
                return Task.CompletedTask;
            });

        Assert.Equal(expected, paymentProcessor.OrderCreated);
        Assert.Equal(1, completions);
    }

    [Fact]
    public async Task ProcessAsyncProcessesThenCompletesAValidPendingOrder()
    {
        var calls = new List<string>();
        var expected = CreateOrderCreated(OrderCreated.PendingStatus);
        var paymentProcessor = new CapturingPaymentProcessor(calls);
        var messageProcessor = CreateMessageProcessor(paymentProcessor);

        await messageProcessor.ProcessAsync(
            Serialize(expected),
            "message-123",
            expected.OrderId.ToString("D"),
            OrderCreated.Subject,
            OrderCreated.JsonContentType,
            _ =>
            {
                calls.Add("complete");
                return Task.CompletedTask;
            });

        Assert.Equal(expected, paymentProcessor.OrderCreated);
        Assert.Equal(["payment", "complete"], calls);
    }

    [Fact]
    public async Task ProcessAsyncSkipsDuplicatePaymentButCompletesBothDeliveries()
    {
        var orderCreated = CreateOrderCreated(OrderCreated.PendingStatus);
        var paymentProcessor = new CapturingPaymentProcessor();
        var processedMessages = new InMemoryProcessedMessageStore();
        var messageProcessor = CreateMessageProcessor(paymentProcessor, processedMessages);
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

        Assert.Equal(1, paymentProcessor.CallCount);
        Assert.Equal(2, completions);
        Assert.Equal([ConsumerIdentities.Payment, ConsumerIdentities.Payment], processedMessages.ConsumerNames);
    }

    [Fact]
    public async Task ProcessAsyncDoesNotReexecutePaymentWhenCompletionFailsAfterProcessing()
    {
        var orderCreated = CreateOrderCreated(OrderCreated.PendingStatus);
        var paymentProcessor = new CapturingPaymentProcessor();
        var processedMessages = new InMemoryProcessedMessageStore();
        var messageProcessor = CreateMessageProcessor(paymentProcessor, processedMessages);
        var completionAttempts = 0;
        var deadLetters = 0;

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
                (_, _) =>
                {
                    deadLetters++;
                    return Task.CompletedTask;
                }));

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
                (_, _) =>
                {
                    deadLetters++;
                    return Task.CompletedTask;
                });

        Assert.Equal(1, paymentProcessor.CallCount);
        Assert.Equal(2, completionAttempts);
        Assert.Equal(0, deadLetters);
    }

    [Fact]
    public async Task ProcessAsyncDeadLettersUnsupportedPaymentStatusWithoutCompletedInboxState()
    {
        var orderCreated = CreateOrderCreated("Cancelled");
        var processedMessages = new InMemoryProcessedMessageStore();
        var messageProcessor = CreateMessageProcessor(new SimulatedPaymentProcessor(), processedMessages);
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
        Assert.Contains("Payment", deadLetter.Description);
        Assert.Contains("message-123", deadLetter.Description);
        Assert.Contains("Cancelled", deadLetter.Description);
        Assert.False(processedMessages.IsCompleted(ConsumerIdentities.Payment, "message-123"));
    }

    [Fact]
    public async Task ProcessAsyncDeadLettersAnInvalidPaymentContract()
    {
        var paymentProcessor = new CapturingPaymentProcessor();
        var messageProcessor = CreateMessageProcessor(paymentProcessor);
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

        Assert.Null(paymentProcessor.OrderCreated);
        Assert.Equal(0, completions);
        Assert.NotNull(deadLetter);
        Assert.Equal(MessageDeadLetterDetailsFactory.MessageContractViolationReason, deadLetter.Reason);
        Assert.Contains("Payment", deadLetter.Description);
        Assert.Contains("message-123", deadLetter.Description);
        Assert.Contains("Unexpected", deadLetter.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessAsyncDeadLettersPaymentJsonThatCannotBeDeserialized()
    {
        var paymentProcessor = new CapturingPaymentProcessor();
        var messageProcessor = CreateMessageProcessor(paymentProcessor);
        var completions = 0;
        MessageDeadLetterDetails? deadLetter = null;

        await messageProcessor.ProcessAsync(
            "not-json"u8.ToArray(),
            "message-123",
            "correlation-456",
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

        Assert.Null(paymentProcessor.OrderCreated);
        Assert.Equal(0, completions);
        Assert.NotNull(deadLetter);
        Assert.Equal(MessageDeadLetterDetailsFactory.MessageContractViolationReason, deadLetter.Reason);
        Assert.Contains("JsonException", deadLetter.Description);
    }

    [Fact]
    public async Task ProcessAsyncLeavesTransientPaymentFailureUnsettledAndRetryable()
    {
        var orderCreated = CreateOrderCreated(OrderCreated.PendingStatus);
        var paymentProcessor = new FailingOncePaymentProcessor();
        var processedMessages = new InMemoryProcessedMessageStore();
        var messageProcessor = CreateMessageProcessor(paymentProcessor, processedMessages);
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
        Assert.False(processedMessages.IsCompleted(ConsumerIdentities.Payment, "message-transient"));

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

        Assert.Equal(2, paymentProcessor.CallCount);
        Assert.Equal(1, completions);
        Assert.Equal(0, deadLetters);
        Assert.True(processedMessages.IsCompleted(ConsumerIdentities.Payment, "message-transient"));
    }

    [Fact]
    public async Task ProcessAsyncPropagatesDeadLetterSettlementFailure()
    {
        var paymentProcessor = new CapturingPaymentProcessor();
        var messageProcessor = CreateMessageProcessor(paymentProcessor);
        var completions = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => messageProcessor.ProcessAsync(
                Serialize(CreateOrderCreated(OrderCreated.PendingStatus)),
                "message-dlq-settlement-failure",
                "correlation-456",
                "CloudOrders.Orders.Unexpected",
                OrderCreated.JsonContentType,
                _ =>
                {
                    completions++;
                    return Task.CompletedTask;
                },
                (_, _) => Task.FromException(new InvalidOperationException("Dead-letter settlement failed."))));

        Assert.Null(paymentProcessor.OrderCreated);
        Assert.Equal(0, completions);
    }

    [Fact]
    public async Task ProcessAsyncEmitsCorrelatedWorkerTraceAndLowCardinalityMetrics()
    {
        Activity? captured = null;
        var metricTags = new List<KeyValuePair<string, object?>[]>();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CloudOrdersTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured = activity
        };
        ActivitySource.AddActivityListener(activityListener);

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == CloudOrdersTelemetry.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var copiedTags = new KeyValuePair<string, object?>[tags.Length];
            tags.CopyTo(copiedTags);
            metricTags.Add(copiedTags);
        });
        meterListener.Start();

        var orderCreated = CreateOrderCreated(OrderCreated.PendingStatus);
        var processor = CreateMessageProcessor(new CapturingPaymentProcessor());

        await processor.ProcessAsync(
            Serialize(orderCreated),
            "message-observability",
            orderCreated.OrderId.ToString("D"),
            OrderCreated.Subject,
            OrderCreated.JsonContentType,
            _ => Task.CompletedTask,
            deliveryCount: 2);

        Assert.NotNull(captured);
        Assert.Equal("cloudorders.messaging.process", captured!.OperationName);
        Assert.Equal(ConsumerIdentities.Payment, captured.GetTagItem("cloudorders.consumer"));
        Assert.Equal(orderCreated.OrderId.ToString("D"), captured.GetTagItem("cloudorders.order.id"));
        Assert.Equal("message-observability", captured.GetTagItem("messaging.message.id"));
        Assert.Equal(orderCreated.OrderId.ToString("D"), captured.GetTagItem("messaging.conversation.id"));
        Assert.Equal("processed", captured.GetTagItem("cloudorders.outcome"));
        Assert.NotEmpty(metricTags);
        Assert.All(
            metricTags.SelectMany(tags => tags),
            tag => Assert.DoesNotContain("id", tag.Key, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SimulatedPaymentProcessorReturnsAStableReferenceForPendingOrders()
    {
        var orderCreated = CreateOrderCreated(OrderCreated.PendingStatus);
        var paymentProcessor = new SimulatedPaymentProcessor();

        var first = await paymentProcessor.ProcessAsync(orderCreated);
        var second = await paymentProcessor.ProcessAsync(orderCreated);

        Assert.Equal(orderCreated.OrderId, first.OrderId);
        Assert.Equal($"simulated-payment-{orderCreated.OrderId:N}", first.PaymentReference);
        Assert.Equal(first, second);
    }

    private static PaymentMessageProcessor CreateMessageProcessor(
        IPaymentProcessor paymentProcessor,
        IProcessedMessageStore? processedMessageStore = null)
    {
        return new PaymentMessageProcessor(
            paymentProcessor,
            processedMessageStore ?? new InMemoryProcessedMessageStore(),
            NullLogger<PaymentMessageProcessor>.Instance);
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

    private sealed class CapturingPaymentProcessor(List<string>? calls = null) : IPaymentProcessor
    {
        public OrderCreated? OrderCreated { get; private set; }

        public int CallCount { get; private set; }

        public Task<SimulatedPaymentReceipt> ProcessAsync(
            OrderCreated orderCreated,
            CancellationToken cancellationToken = default)
        {
            OrderCreated = orderCreated;
            CallCount++;
            calls?.Add("payment");

            return Task.FromResult(new SimulatedPaymentReceipt(
                orderCreated.OrderId,
                "captured-payment"));
        }
    }

    private sealed class FailingOncePaymentProcessor : IPaymentProcessor
    {
        public int CallCount { get; private set; }

        public Task<SimulatedPaymentReceipt> ProcessAsync(
            OrderCreated orderCreated,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            return CallCount == 1
                ? Task.FromException<SimulatedPaymentReceipt>(new InvalidOperationException("Simulated transient payment failure."))
                : Task.FromResult(new SimulatedPaymentReceipt(orderCreated.OrderId, "retried-payment"));
        }
    }
}
