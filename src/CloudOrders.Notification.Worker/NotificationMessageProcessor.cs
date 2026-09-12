using System.Text.Json;
using CloudOrders.Application.Messaging;

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completeMessageAsync);
        Guid? orderId = null;

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
            var execution = await processedMessageStore
                .ExecuteOnceAsync(
                    ConsumerIdentities.Notification,
                    messageId,
                    cancellationToken => notificationProcessor.ProcessAsync(orderCreated, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            if (execution.WasAlreadyProcessed)
            {
                LogSkippedDuplicateNotification(orderCreated.OrderId, messageId, correlationId);
            }
            else
            {
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
            await deadLetterMessageAsync(details, cancellationToken).ConfigureAwait(false);
            LogDeadLetteredNotification(messageId, correlationId, details.Reason);
            return;
        }

        await completeMessageAsync(cancellationToken).ConfigureAwait(false);

        LogCompletedNotification(orderId!.Value, messageId, correlationId);
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
        Message = "Dead-lettered notification message {MessageId} with correlation {CorrelationId} and reason {DeadLetterReason}.")]
    private partial void LogDeadLetteredNotification(
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
}
