using System.Text;
using CloudOrders.Application.Messaging;
using Microsoft.EntityFrameworkCore;

namespace CloudOrders.Infrastructure.Persistence;

internal sealed class EfOutboxMessageStore(CloudOrdersDbContext dbContext) : IOutboxMessageStore
{
    public async Task<IReadOnlyList<PendingOutboxMessage>> GetPendingAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        var messages = await dbContext.OutboxMessages
            .AsNoTracking()
            .Where(item => item.PublishedAtUtc == null)
            .OrderBy(item => item.CreatedAtUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return messages.Select(item => new PendingOutboxMessage(
            item.Id,
            item.OrderId,
            new OutboundMessage(
                item.MessageId,
                item.CorrelationId,
                item.Subject,
                item.ContentType,
                Encoding.UTF8.GetBytes(item.PayloadJson)),
            string.IsNullOrWhiteSpace(item.TraceParent)
                ? null
                : new OutboxTraceContext(item.TraceParent, item.TraceState)))
            .ToArray();
    }

    public async Task MarkPublishedAsync(
        Guid id,
        DateTimeOffset attemptedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await dbContext.OutboxMessages
            .Where(item => item.Id == id && item.PublishedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.PublishedAtUtc, attemptedAtUtc)
                    .SetProperty(item => item.LastAttemptAtUtc, attemptedAtUtc)
                    .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1)
                    .SetProperty(item => item.LastErrorType, (string?)null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RecordFailedAttemptAsync(
        Guid id,
        DateTimeOffset attemptedAtUtc,
        string failureType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureType);

        await dbContext.OutboxMessages
            .Where(item => item.Id == id && item.PublishedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.LastAttemptAtUtc, attemptedAtUtc)
                    .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1)
                    .SetProperty(item => item.LastErrorType, failureType),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<long> CountPendingAsync(CancellationToken cancellationToken = default)
    {
        return dbContext.OutboxMessages.LongCountAsync(
            item => item.PublishedAtUtc == null,
            cancellationToken);
    }
}
