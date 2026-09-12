namespace CloudOrders.Application.Messaging;

public sealed record ProcessedMessageExecution<T>(bool WasAlreadyProcessed, T? Result);

public interface IProcessedMessageStore
{
    Task<ProcessedMessageExecution<T>> ExecuteOnceAsync<T>(
        string consumerName,
        string messageId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);
}
