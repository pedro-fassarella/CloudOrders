using System.Text.Json;
using CloudOrders.Application.Messaging;

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
                    ConsumerIdentities.Payment,
                    messageId,
                    cancellationToken => paymentProcessor.ProcessAsync(orderCreated, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            if (execution.WasAlreadyProcessed)
            {
                LogSkippedDuplicatePayment(orderCreated.OrderId, messageId, correlationId);
            }
            else
            {
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
            await deadLetterMessageAsync(details, cancellationToken).ConfigureAwait(false);
            LogDeadLetteredPayment(messageId, correlationId, details.Reason);
            return;
        }

        await completeMessageAsync(cancellationToken).ConfigureAwait(false);

        LogCompletedPayment(orderId!.Value, messageId, correlationId);
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
        Message = "Dead-lettered payment message {MessageId} with correlation {CorrelationId} and reason {DeadLetterReason}.")]
    private partial void LogDeadLetteredPayment(
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
}
