using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace CloudOrders.Application.Observability;

public static class CloudOrdersTelemetry
{
    public const string ActivitySourceName = "CloudOrders";

    public const string MeterName = "CloudOrders";

    public const string ApiServiceName = "cloudorders-api";

    public const string PaymentWorkerServiceName = "cloudorders-payment-worker";

    public const string InventoryWorkerServiceName = "cloudorders-inventory-worker";

    public const string NotificationWorkerServiceName = "cloudorders-notification-worker";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> OrdersPersisted = Meter.CreateCounter<long>("cloudorders.orders.persisted");
    private static readonly Counter<long> OrdersPublished = Meter.CreateCounter<long>("cloudorders.orders.published");
    private static readonly Counter<long> MessagesProcessed = Meter.CreateCounter<long>("cloudorders.messaging.processed");
    private static readonly Histogram<double> MessageProcessingDuration = Meter.CreateHistogram<double>("cloudorders.messaging.processing.duration", "s");
    private static readonly Counter<long> MessageSettlements = Meter.CreateCounter<long>("cloudorders.messaging.settlements");
    private static readonly Counter<long> MessagesDeadLettered = Meter.CreateCounter<long>("cloudorders.messaging.dead_lettered");
    private static readonly Counter<long> MessageRedeliveries = Meter.CreateCounter<long>("cloudorders.messaging.redeliveries");
    private static readonly Counter<long> ProcessorErrors = Meter.CreateCounter<long>("cloudorders.servicebus.processor.errors");

    public static Activity? StartOrderCreation(Guid orderId, string messageId, string? correlationId)
    {
        var activity = ActivitySource.StartActivity("cloudorders.order.create", ActivityKind.Internal);
        AddMessageTags(activity, orderId, messageId, correlationId);
        return activity;
    }

    public static Activity? StartMessageProcessing(
        string consumer,
        string messageId,
        string? correlationId,
        int deliveryCount)
    {
        var activity = ActivitySource.StartActivity("cloudorders.messaging.process", ActivityKind.Internal);
        activity?.SetTag("cloudorders.consumer", consumer);
        activity?.SetTag("messaging.message.id", messageId);
        activity?.SetTag("messaging.conversation.id", correlationId);
        activity?.SetTag("cloudorders.delivery_count", deliveryCount);
        return activity;
    }

    public static void AddOrderId(Activity? activity, Guid orderId)
    {
        activity?.SetTag("cloudorders.order.id", orderId.ToString("D"));
    }

    public static void SetOutcome(Activity? activity, string outcome)
    {
        activity?.SetTag("cloudorders.outcome", outcome);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public static void SetFailure(Activity? activity, Exception exception)
    {
        activity?.SetTag("cloudorders.outcome", "failed");
        activity?.SetTag("error.type", exception.GetType().FullName);
        activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
    }

    public static void RecordOrderPersisted() => OrdersPersisted.Add(1);

    public static void RecordOrderPublished() => OrdersPublished.Add(1);

    public static void RecordMessageProcessing(string consumer, string outcome, double durationSeconds)
    {
        var tags = new TagList
        {
            { "consumer", consumer },
            { "outcome", outcome }
        };

        MessagesProcessed.Add(1, tags);
        MessageProcessingDuration.Record(durationSeconds, tags);
    }

    public static void RecordMessageSettlement(string consumer, string settlement, string outcome)
    {
        MessageSettlements.Add(1, new TagList
        {
            { "consumer", consumer },
            { "settlement", settlement },
            { "outcome", outcome }
        });
    }

    public static void RecordMessageDeadLettered(string consumer, string reason)
    {
        MessagesDeadLettered.Add(1, new TagList
        {
            { "consumer", consumer },
            { "reason", reason }
        });
    }

    public static void RecordRedelivery(string consumer)
    {
        MessageRedeliveries.Add(1, new TagList { { "consumer", consumer } });
    }

    public static void RecordProcessorError(string consumer, string errorSource)
    {
        ProcessorErrors.Add(1, new TagList
        {
            { "consumer", consumer },
            { "error_source", errorSource }
        });
    }

    public static IDisposable? BeginOrderScope(
        ILogger logger,
        string service,
        Guid orderId,
        string messageId,
        string? correlationId)
    {
        return logger.BeginScope(CreateScope(
            service,
            "CreateOrder",
            consumer: null,
            orderId,
            messageId,
            correlationId,
            deliveryCount: null));
    }

    public static IDisposable? BeginMessageScope(
        ILogger logger,
        string service,
        string consumer,
        string messageId,
        string? correlationId,
        int deliveryCount)
    {
        return logger.BeginScope(CreateScope(
            service,
            "ProcessOrderCreated",
            consumer,
            orderId: null,
            messageId,
            correlationId,
            deliveryCount));
    }

    public static IDisposable? BeginProcessorScope(ILogger logger, string service, string consumer)
    {
        return logger.BeginScope(CreateScope(
            service,
            "ServiceBusProcessor",
            consumer,
            orderId: null,
            messageId: null,
            correlationId: null,
            deliveryCount: null));
    }

    private static void AddMessageTags(Activity? activity, Guid orderId, string messageId, string? correlationId)
    {
        activity?.SetTag("cloudorders.order.id", orderId.ToString("D"));
        activity?.SetTag("messaging.message.id", messageId);
        activity?.SetTag("messaging.conversation.id", correlationId);
    }

    private static Dictionary<string, object?> CreateScope(
        string service,
        string operation,
        string? consumer,
        Guid? orderId,
        string? messageId,
        string? correlationId,
        int? deliveryCount)
    {
        var activity = Activity.Current;

        return new Dictionary<string, object?>
        {
            ["Service"] = service,
            ["Operation"] = operation,
            ["Consumer"] = consumer,
            ["OrderId"] = orderId?.ToString("D"),
            ["MessageId"] = messageId,
            ["CorrelationId"] = correlationId,
            ["DeliveryCount"] = deliveryCount,
            ["TraceId"] = activity?.TraceId.ToString(),
            ["SpanId"] = activity?.SpanId.ToString()
        };
    }
}
