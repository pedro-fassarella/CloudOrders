using CloudOrders.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CloudOrders.Api;

internal sealed class CloudOrdersDbContextFactory : IDesignTimeDbContextFactory<CloudOrdersDbContext>
{
    public CloudOrdersDbContext CreateDbContext(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(Program).Assembly.GetName().Name
        });
        var connectionString = builder.Configuration.GetConnectionString("Postgres");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Postgres must be configured before using persistence.");
        }

        var options = new DbContextOptionsBuilder<CloudOrdersDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new CloudOrdersDbContext(options);
    }
}
