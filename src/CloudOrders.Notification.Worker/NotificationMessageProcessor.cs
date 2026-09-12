using System.Diagnostics;
using System.Text.Json;
using CloudOrders.Application.Messaging;
using CloudOrders.Application.Observability;

namespace CloudOrders.Notification.Worker;

internal sealed partial class NotificationMessageProcessor(
    INotificationProcessor notificationProcessor,
    IProcessedMessageStore processedMessageStore,
    ILogger<NotificationMessageProcessor> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task ProcessAsync(
        ReadOnlyMemory<byte> body,
        string messageId,
        string? correlationId,
        string? subject,
        string? contentType,
        Func<CancellationToken, Task> completeMessageAsync,
        Func<MessageDeadLetterDetails, CancellationToken, Task>? deadLetterMessageAsync = null,
        CancellationToken cancellationToken = default,
        int deliveryCount = 1)
    {
        ArgumentNullException.ThrowIfNull(completeMessageAsync);
        Guid? orderId = null;
        var outcome = "failed";
        string? settlement = null;
        var startedAt = Stopwatch.GetTimestamp();

        using var activity = CloudOrdersTelemetry.StartMessageProcessing(
            ConsumerIdentities.Notification,
            messageId,
            correlationId,
            deliveryCount);
        using var scope = CloudOrdersTelemetry.BeginMessageScope(
            logger,
            CloudOrdersTelemetry.NotificationWorkerServiceName,
            ConsumerIdentities.Notification,
            messageId,
            correlationId,
            deliveryCount);

        if (deliveryCount > 1)
        {
            CloudOrdersTelemetry.RecordRedelivery(ConsumerIdentities.Notification);
        }

        try
        {
            try
            {
                if (string.IsNullOrWhiteSpace(messageId))
                {
                    throw new PermanentMessageFailureException(
                        PermanentMessageFailureKind.ContractViolation,
                        "The Service Bus message ID was missing.");
                }

                ValidateMessageContract(subject, contentType);

                if (body.IsEmpty)
                {
                    throw new PermanentMessageFailureException(
                        PermanentMessageFailureKind.ContractViolation,
                        "The OrderCreated message body was empty.");
                }

                var orderCreated = DeserializeOrderCreated(body);
                orderId = orderCreated.OrderId;
                CloudOrdersTelemetry.AddOrderId(activity, orderCreated.OrderId);
                var execution = await processedMessageStore
                    .ExecuteOnceAsync(
                        ConsumerIdentities.Notification,
                        messageId,
                        cancellationToken => notificationProcessor.ProcessAsync(orderCreated, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (execution.WasAlreadyProcessed)
                {
                    outcome = "duplicate";
                    LogSkippedDuplicateNotification(orderCreated.OrderId, messageId, correlationId);
                }
                else
                {
                    outcome = "processed";
                    var receipt = execution.Result
                        ?? throw new InvalidOperationException("Notification processing completed without a receipt.");

                    LogProcessedNotification(
                        orderCreated.OrderId,
                        messageId,
                        correlationId,
                        receipt.NotificationChannel,
                        receipt.NotificationReference);
                }
            }
            catch (PermanentMessageFailureException failure) when (deadLetterMessageAsync is not null)
            {
                var details = MessageDeadLetterDetailsFactory.Create(
                    ConsumerIdentities.Notification,
                    messageId,
                    failure);
                settlement = "dead_letter";
                await deadLetterMessageAsync(details, cancellationToken).ConfigureAwait(false);
                CloudOrdersTelemetry.RecordMessageSettlement(
                    ConsumerIdentities.Notification,
                    settlement,
                    "succeeded");
                CloudOrdersTelemetry.RecordMessageDeadLettered(ConsumerIdentities.Notification, details.Reason);
                outcome = "dead_lettered";
                CloudOrdersTelemetry.SetOutcome(activity, outcome);
                LogDeadLetteredNotification(orderId, messageId, correlationId, details.Reason);
                return;
            }

            settlement = "complete";
            await completeMessageAsync(cancellationToken).ConfigureAwait(false);
            CloudOrdersTelemetry.RecordMessageSettlement(
                ConsumerIdentities.Notification,
                settlement,
                "succeeded");
            CloudOrdersTelemetry.SetOutcome(activity, outcome);
            LogCompletedNotification(orderId!.Value, messageId, correlationId);
        }
        catch (Exception exception)
        {
            if (settlement is not null)
            {
                CloudOrdersTelemetry.RecordMessageSettlement(
                    ConsumerIdentities.Notification,
                    settlement,
                    "failed");
            }

            CloudOrdersTelemetry.SetFailure(activity, exception);
            LogNotificationProcessingFailed(exception, exception.GetType().Name, orderId);
            throw;
        }
        finally
        {
            CloudOrdersTelemetry.RecordMessageProcessing(
                ConsumerIdentities.Notification,
                outcome,
                Stopwatch.GetElapsedTime(startedAt).TotalSeconds);
        }
    }

    private static void ValidateMessageContract(string? subject, string? contentType)
    {
        if (!string.Equals(subject, OrderCreated.Subject, StringComparison.Ordinal))
        {
            throw new PermanentMessageFailureException(
                PermanentMessageFailureKind.ContractViolation,
                $"Unexpected message subject '{subject}'.");
        }

        if (!string.Equals(
                contentType,
                OrderCreated.JsonContentType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PermanentMessageFailureException(
                PermanentMessageFailureKind.ContractViolation,
                $"Unexpected content type '{contentType}'.");
        }
    }

    private static OrderCreated DeserializeOrderCreated(ReadOnlyMemory<byte> body)
    {
        try
        {
            return JsonSerializer.Deserialize<OrderCreated>(body.Span, SerializerOptions)
                ?? throw new PermanentMessageFailureException(
                    PermanentMessageFailureKind.ContractViolation,
                    "The OrderCreated message body was empty.");
        }
        catch (JsonException exception)
        {
            throw new PermanentMessageFailureException(
                PermanentMessageFailureKind.ContractViolation,
                "The OrderCreated message body could not be deserialized.",
                exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Processed simulated notification for order {OrderId} from message {MessageId} with correlation {CorrelationId}, channel {NotificationChannel}, and reference {NotificationReference}.")]
    private partial void LogProcessedNotification(
        Guid orderId,
        string messageId,
        string? correlationId,
        string notificationChannel,
        string notificationReference);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Skipped duplicate notification message {MessageId} for order {OrderId} with correlation {CorrelationId}.")]
    private partial void LogSkippedDuplicateNotification(
        Guid orderId,
        string messageId,
        string? correlationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Dead-lettered notification message {MessageId} for order {OrderId} with correlation {CorrelationId} and reason {DeadLetterReason}.")]
    private partial void LogDeadLetteredNotification(
        Guid? orderId,
        string messageId,
        string? correlationId,
        string deadLetterReason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Completed notification message {MessageId} for order {OrderId} with correlation {CorrelationId}.")]
    private partial void LogCompletedNotification(
        Guid orderId,
        string messageId,
        string? correlationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Notification message processing failed for order {OrderId} with {FailureType}.")]
    private partial void LogNotificationProcessingFailed(Exception exception, string failureType, Guid? orderId);
}
