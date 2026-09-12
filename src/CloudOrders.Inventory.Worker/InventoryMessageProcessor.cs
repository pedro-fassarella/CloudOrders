using System.Diagnostics;
using System.Text.Json;
using CloudOrders.Application.Messaging;
using CloudOrders.Application.Observability;

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
        CancellationToken cancellationToken = default,
        int deliveryCount = 1)
    {
        ArgumentNullException.ThrowIfNull(completeMessageAsync);
        Guid? orderId = null;
        var outcome = "failed";
        string? settlement = null;
        var startedAt = Stopwatch.GetTimestamp();

        using var activity = CloudOrdersTelemetry.StartMessageProcessing(
            ConsumerIdentities.Inventory,
            messageId,
            correlationId,
            deliveryCount);
        using var scope = CloudOrdersTelemetry.BeginMessageScope(
            logger,
            CloudOrdersTelemetry.InventoryWorkerServiceName,
            ConsumerIdentities.Inventory,
            messageId,
            correlationId,
            deliveryCount);

        if (deliveryCount > 1)
        {
            CloudOrdersTelemetry.RecordRedelivery(ConsumerIdentities.Inventory);
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
                        ConsumerIdentities.Inventory,
                        messageId,
                        cancellationToken => inventoryProcessor.ProcessAsync(orderCreated, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (execution.WasAlreadyProcessed)
                {
                    outcome = "duplicate";
                    LogSkippedDuplicateInventoryMessage(orderCreated.OrderId, messageId, correlationId);
                }
                else
                {
                    outcome = "processed";
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
                settlement = "dead_letter";
                await deadLetterMessageAsync(details, cancellationToken).ConfigureAwait(false);
                CloudOrdersTelemetry.RecordMessageSettlement(
                    ConsumerIdentities.Inventory,
                    settlement,
                    "succeeded");
                CloudOrdersTelemetry.RecordMessageDeadLettered(ConsumerIdentities.Inventory, details.Reason);
                outcome = "dead_lettered";
                CloudOrdersTelemetry.SetOutcome(activity, outcome);
                LogDeadLetteredInventoryMessage(orderId, messageId, correlationId, details.Reason);
                return;
            }

            settlement = "complete";
            await completeMessageAsync(cancellationToken).ConfigureAwait(false);
            CloudOrdersTelemetry.RecordMessageSettlement(
                ConsumerIdentities.Inventory,
                settlement,
                "succeeded");
            CloudOrdersTelemetry.SetOutcome(activity, outcome);
            LogCompletedInventoryMessage(orderId!.Value, messageId, correlationId);
        }
        catch (Exception exception)
        {
            if (settlement is not null)
            {
                CloudOrdersTelemetry.RecordMessageSettlement(
                    ConsumerIdentities.Inventory,
                    settlement,
                    "failed");
            }

            CloudOrdersTelemetry.SetFailure(activity, exception);
            LogInventoryProcessingFailed(exception, exception.GetType().Name, orderId);
            throw;
        }
        finally
        {
            CloudOrdersTelemetry.RecordMessageProcessing(
                ConsumerIdentities.Inventory,
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
        Message = "Dead-lettered inventory message {MessageId} for order {OrderId} with correlation {CorrelationId} and reason {DeadLetterReason}.")]
    private partial void LogDeadLetteredInventoryMessage(
        Guid? orderId,
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

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Inventory message processing failed for order {OrderId} with {FailureType}.")]
    private partial void LogInventoryProcessingFailed(Exception exception, string failureType, Guid? orderId);
}
