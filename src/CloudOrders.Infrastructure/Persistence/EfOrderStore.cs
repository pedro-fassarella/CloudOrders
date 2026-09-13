using CloudOrders.Application.Orders;
using CloudOrders.Application.Messaging;
using CloudOrders.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CloudOrders.Infrastructure.Persistence;

internal sealed class EfOrderStore(
    CloudOrdersDbContext dbContext,
    TimeProvider timeProvider) : IOrderStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task AddWithOutboxAsync(
        Order order,
        OrderCreated orderCreated,
        MessageMetadata metadata,
        OutboxTraceContext? traceContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(orderCreated);
        ArgumentNullException.ThrowIfNull(metadata);

        var outboxMessage = new OutboxMessage(
            Guid.NewGuid(),
            order.Id,
            metadata,
            JsonSerializer.Serialize(orderCreated, SerializerOptions),
            timeProvider.GetUtcNow(),
            traceContext);

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            dbContext.Orders.Add(order);
            dbContext.OutboxMessages.Add(outboxMessage);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Orders
            .AsNoTracking()
            .SingleOrDefaultAsync(order => order.Id == id, cancellationToken);
    }
}
