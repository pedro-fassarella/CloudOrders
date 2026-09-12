namespace CloudOrders.Application.Messaging;

public enum PermanentMessageFailureKind
{
    ContractViolation,
    UnsupportedBusinessStatus
}

public class PermanentMessageFailureException(
    PermanentMessageFailureKind kind,
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public PermanentMessageFailureKind Kind { get; } = kind;
}

public sealed record MessageDeadLetterDetails(string Reason, string Description);

public static class MessageDeadLetterDetailsFactory
{
    public const string MessageContractViolationReason = "MessageContractViolation";

    public const string UnsupportedBusinessStatusReason = "UnsupportedBusinessStatus";

    private const int MaximumDescriptionLength = 4096;

    public static MessageDeadLetterDetails Create(
        string consumerName,
        string? messageId,
        PermanentMessageFailureException failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentNullException.ThrowIfNull(failure);

        var reason = failure.Kind switch
        {
            PermanentMessageFailureKind.ContractViolation => MessageContractViolationReason,
            PermanentMessageFailureKind.UnsupportedBusinessStatus => UnsupportedBusinessStatusReason,
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure.Kind, "Unknown permanent failure kind.")
        };

        var exceptionType = failure.InnerException?.GetType().Name ?? failure.GetType().Name;
        var displayMessageId = string.IsNullOrWhiteSpace(messageId) ? "<missing>" : messageId;
        var description =
            $"Consumer '{consumerName}' rejected message '{displayMessageId}' with {exceptionType}: {failure.Message}";

        if (description.Length > MaximumDescriptionLength)
        {
            description = string.Concat(description[..(MaximumDescriptionLength - 3)], "...");
        }

        return new MessageDeadLetterDetails(reason, description);
    }
}
