using CloudOrders.Application.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CloudOrders.Infrastructure.Persistence;

internal sealed class EfProcessedMessageStore(IDbContextFactory<CloudOrdersDbContext> dbContextFactory)
    : IProcessedMessageStore
{
    public async Task<ProcessedMessageExecution<T>> ExecuteOnceAsync<T>(
        string consumerName,
        string messageId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(operation);

        await using var dbContext = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var startedAtUtc = TimeProvider.System.GetUtcNow();
            var claimAcquired = await TryAcquireClaimAsync(
                dbContext,
                transaction,
                consumerName,
                messageId,
                startedAtUtc,
                cancellationToken).ConfigureAwait(false);

            if (!claimAcquired)
            {
                var existing = await dbContext.ProcessedMessages
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        item => item.ConsumerName == consumerName && item.MessageId == messageId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (existing?.State == ProcessedMessageState.Completed)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new ProcessedMessageExecution<T>(true, default);
                }

                throw new InvalidOperationException(
                    $"Processed message '{consumerName}/{messageId}' was not completed after a conflicting claim.");
            }

            var result = await operation(cancellationToken).ConfigureAwait(false);
            var completedAtUtc = TimeProvider.System.GetUtcNow();
            var completed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE processed_messages
                SET state = {ProcessedMessageState.Completed.ToString()},
                    completed_at_utc = {completedAtUtc}
                WHERE consumer_name = {consumerName}
                  AND message_id = {messageId}
                  AND state = {ProcessedMessageState.Processing.ToString()};
                """,
                cancellationToken).ConfigureAwait(false);

            if (completed != 1)
            {
                throw new InvalidOperationException(
                    $"Processed message '{consumerName}/{messageId}' could not be marked as completed.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessedMessageExecution<T>(false, result);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<bool> TryAcquireClaimAsync(
        CloudOrdersDbContext dbContext,
        IDbContextTransaction transaction,
        string consumerName,
        string messageId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();

        await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
                INSERT INTO processed_messages
                    (consumer_name, message_id, state, started_at_utc, completed_at_utc)
                VALUES
                    (@consumerName, @messageId, @state, @startedAtUtc, NULL)
                ON CONFLICT (consumer_name, message_id) DO NOTHING
                RETURNING 1;
                """;
        AddParameter(command, "@consumerName", consumerName);
        AddParameter(command, "@messageId", messageId);
        AddParameter(command, "@state", ProcessedMessageState.Processing.ToString());
        AddParameter(command, "@startedAtUtc", startedAtUtc);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
