using Microsoft.Extensions.DependencyInjection;

namespace CloudOrders.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<Orders.CreateOrderHandler>();
        services.AddScoped<Orders.GetOrderHandler>();
        return services;
    }
}
