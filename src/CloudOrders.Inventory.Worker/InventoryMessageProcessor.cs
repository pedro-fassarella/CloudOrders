using System.Text.Json;
using CloudOrders.Application.Messaging;

namespace CloudOrders.Inventory.Worker;

internal sealed partial class InventoryMessageProcessor(
    IInventoryProcessor inventoryProcessor,
    IProcessedMessageStore processedMessageStore,
    ILogger<InventoryMessageProcessor> logger)
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
                    ConsumerIdentities.Inventory,
                    messageId,
                    cancellationToken => inventoryProcessor.ProcessAsync(orderCreated, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            if (execution.WasAlreadyProcessed)
            {
                LogSkippedDuplicateInventoryMessage(orderCreated.OrderId, messageId, correlationId);
            }
            else
            {
                var reservation = execution.Result
                    ?? throw new InvalidOperationException("Inventory processing completed without a reservation.");

                LogProcessedInventoryReservation(
                    orderCreated.OrderId,
                    messageId,
                    correlationId,
                    reservation.ReservationReference);
            }
        }
        catch (PermanentMessageFailureException failure) when (deadLetterMessageAsync is not null)
        {
            var details = MessageDeadLetterDetailsFactory.Create(
                ConsumerIdentities.Inventory,
                messageId,
                failure);
            await deadLetterMessageAsync(details, cancellationToken).ConfigureAwait(false);
            LogDeadLetteredInventoryMessage(messageId, correlationId, details.Reason);
            return;
        }

        await completeMessageAsync(cancellationToken).ConfigureAwait(false);

        LogCompletedInventoryMessage(orderId!.Value, messageId, correlationId);
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
        Message = "Processed simulated inventory reservation for order {OrderId} from message {MessageId} with correlation {CorrelationId} and reference {ReservationReference}.")]
    private partial void LogProcessedInventoryReservation(
        Guid orderId,
        string messageId,
        string? correlationId,
        string reservationReference);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Skipped duplicate inventory message {MessageId} for order {OrderId} with correlation {CorrelationId}.")]
    private partial void LogSkippedDuplicateInventoryMessage(
        Guid orderId,
        string messageId,
        string? correlationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Dead-lettered inventory message {MessageId} with correlation {CorrelationId} and reason {DeadLetterReason}.")]
    private partial void LogDeadLetteredInventoryMessage(
        string messageId,
        string? correlationId,
        string deadLetterReason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Completed inventory message {MessageId} for order {OrderId} with correlation {CorrelationId}.")]
    private partial void LogCompletedInventoryMessage(
        Guid orderId,
        string messageId,
        string? correlationId);
}
