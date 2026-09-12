using CloudOrders.Application.Orders;
using CloudOrders.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace CloudOrders.Infrastructure.Persistence;

internal sealed class EfOrderStore(CloudOrdersDbContext dbContext) : IOrderStore
{
    public async Task AddAsync(Order order, CancellationToken cancellationToken = default)
    {
        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Orders
            .AsNoTracking()
            .SingleOrDefaultAsync(order => order.Id == id, cancellationToken);
    }
}
