using System.Diagnostics;
using System.Text.Json;
using CloudOrders.Application.Messaging;
using CloudOrders.Application.Observability;

namespace CloudOrders.Payment.Worker;

internal sealed partial class PaymentMessageProcessor(
    IPaymentProcessor paymentProcessor,
    IProcessedMessageStore processedMessageStore,
    ILogger<PaymentMessageProcessor> logger)
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
            ConsumerIdentities.Payment,
            messageId,
            correlationId,
            deliveryCount);
        using var scope = CloudOrdersTelemetry.BeginMessageScope(
            logger,
            CloudOrdersTelemetry.PaymentWorkerServiceName,
            ConsumerIdentities.Payment,
            messageId,
            correlationId,
            deliveryCount);

        if (deliveryCount > 1)
        {
            CloudOrdersTelemetry.RecordRedelivery(ConsumerIdentities.Payment);
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
                        ConsumerIdentities.Payment,
                        messageId,
                        cancellationToken => paymentProcessor.ProcessAsync(orderCreated, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (execution.WasAlreadyProcessed)
                {
                    outcome = "duplicate";
                    LogSkippedDuplicatePayment(orderCreated.OrderId, messageId, correlationId);
                }
                else
                {
                    outcome = "processed";
                    var receipt = execution.Result
                        ?? throw new InvalidOperationException("Payment processing completed without a receipt.");

                    LogProcessedPayment(
                        orderCreated.OrderId,
                        messageId,
                        correlationId,
                        receipt.PaymentReference);
                }
            }
            catch (PermanentMessageFailureException failure) when (deadLetterMessageAsync is not null)
            {
                var details = MessageDeadLetterDetailsFactory.Create(
                    ConsumerIdentities.Payment,
                    messageId,
                    failure);
                settlement = "dead_letter";
                await deadLetterMessageAsync(details, cancellationToken).ConfigureAwait(false);
                CloudOrdersTelemetry.RecordMessageSettlement(
                    ConsumerIdentities.Payment,
                    settlement,
                    "succeeded");
                CloudOrdersTelemetry.RecordMessageDeadLettered(ConsumerIdentities.Payment, details.Reason);
                outcome = "dead_lettered";
                CloudOrdersTelemetry.SetOutcome(activity, outcome);
                LogDeadLetteredPayment(orderId, messageId, correlationId, details.Reason);
                return;
            }

            settlement = "complete";
            await completeMessageAsync(cancellationToken).ConfigureAwait(false);
            CloudOrdersTelemetry.RecordMessageSettlement(
                ConsumerIdentities.Payment,
                settlement,
                "succeeded");
            CloudOrdersTelemetry.SetOutcome(activity, outcome);
            LogCompletedPayment(orderId!.Value, messageId, correlationId);
        }
        catch (Exception exception)
        {
            if (settlement is not null)
            {
                CloudOrdersTelemetry.RecordMessageSettlement(
                    ConsumerIdentities.Payment,
                    settlement,
                    "failed");
            }
            CloudOrdersTelemetry.SetFailure(activity, exception);
            LogPaymentProcessingFailed(exception, exception.GetType().Name, orderId);
            throw;
        }
        finally
        {
            CloudOrdersTelemetry.RecordMessageProcessing(
                ConsumerIdentities.Payment,
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
        Message = "Processed simulated payment for order {OrderId} from message {MessageId} with correlation {CorrelationId} and reference {PaymentReference}.")]
    private partial void LogProcessedPayment(
        Guid orderId,
        string messageId,
        string? correlationId,
        string paymentReference);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Skipped duplicate payment message {MessageId} for order {OrderId} with correlation {CorrelationId}.")]
    private partial void LogSkippedDuplicatePayment(
        Guid orderId,
        string messageId,
        string? correlationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Dead-lettered payment message {MessageId} for order {OrderId} with correlation {CorrelationId} and reason {DeadLetterReason}.")]
    private partial void LogDeadLetteredPayment(
        Guid? orderId,
        string messageId,
        string? correlationId,
        string deadLetterReason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Completed payment message {MessageId} for order {OrderId} with correlation {CorrelationId}.")]
    private partial void LogCompletedPayment(
        Guid orderId,
        string messageId,
        string? correlationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Payment message processing failed for order {OrderId} with {FailureType}.")]
    private partial void LogPaymentProcessingFailed(Exception exception, string failureType, Guid? orderId);
}
