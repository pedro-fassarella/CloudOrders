using CloudOrders.Application.Orders;
using CloudOrders.Application.Messaging;
using CloudOrders.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudOrders.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<CloudOrdersDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("Postgres");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:Postgres must be configured before using persistence.");
            }

            options.UseNpgsql(connectionString);
        });

        services.AddScoped<IOrderStore, EfOrderStore>();
        return services;
    }

    public static IServiceCollection AddProcessedMessagePersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("Postgres");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Postgres must be configured before using processed-message persistence.");
        }

        services.AddDbContextFactory<CloudOrdersDbContext>(options => options.UseNpgsql(connectionString));
        services.AddSingleton<IProcessedMessageStore, EfProcessedMessageStore>();
        return services;
    }
}
