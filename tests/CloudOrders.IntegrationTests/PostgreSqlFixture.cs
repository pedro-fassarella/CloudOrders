using System.Diagnostics.CodeAnalysis;
using CloudOrders.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CloudOrders.IntegrationTests;

[SuppressMessage(
    "Design",
    "CA1515",
    Justification = "xUnit discovers public fixtures by reflection.")]
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17").Build();

    public string ConnectionString => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync().ConfigureAwait(false);

        var options = new DbContextOptionsBuilder<CloudOrdersDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        using var dbContext = new CloudOrdersDbContext(options);
        await dbContext.Database.MigrateAsync().ConfigureAwait(false);
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}
