namespace CloudOrders.Infrastructure.Persistence;

public enum ProcessedMessageState
{
    Processing,
    Completed
}

public sealed class ProcessedMessage
{
    private ProcessedMessage()
    {
    }

    public ProcessedMessage(
        string consumerName,
        string messageId,
        ProcessedMessageState state,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc)
    {
        ConsumerName = consumerName;
        MessageId = messageId;
        State = state;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
    }

    public string ConsumerName { get; private set; } = null!;

    public string MessageId { get; private set; } = null!;

    public ProcessedMessageState State { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }
}
