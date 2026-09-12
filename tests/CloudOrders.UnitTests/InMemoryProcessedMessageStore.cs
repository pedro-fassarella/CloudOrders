using CloudOrders.Application.Messaging;

namespace CloudOrders.UnitTests;

internal sealed class InMemoryProcessedMessageStore : IProcessedMessageStore
{
    private readonly HashSet<(string ConsumerName, string MessageId)> completed = [];
    private readonly List<string> consumerNames = [];

    public IReadOnlyList<string> ConsumerNames => consumerNames;

    public async Task<ProcessedMessageExecution<T>> ExecuteOnceAsync<T>(
        string consumerName,
        string messageId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        lock (completed)
        {
            consumerNames.Add(consumerName);

            if (completed.Contains((consumerName, messageId)))
            {
                return new ProcessedMessageExecution<T>(true, default);
            }
        }

        var result = await operation(cancellationToken).ConfigureAwait(false);

        lock (completed)
        {
            completed.Add((consumerName, messageId));
        }

        return new ProcessedMessageExecution<T>(false, result);
    }

    public bool IsCompleted(string consumerName, string messageId)
    {
        lock (completed)
        {
            return completed.Contains((consumerName, messageId));
        }
    }
}
