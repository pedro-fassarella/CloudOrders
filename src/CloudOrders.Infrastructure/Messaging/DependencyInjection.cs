using Azure.Messaging.ServiceBus;
using CloudOrders.Application.Messaging;
using CloudOrders.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudOrders.Infrastructure.Messaging;

public static class ServiceBusServiceCollectionExtensions
{
    public static IServiceCollection AddServiceBusClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(ServiceBusMessagingOptions.FromConfiguration(configuration));
        services.AddSingleton<ServiceBusClient>(_ =>
        {
            var connectionString = configuration.GetConnectionString("ServiceBus");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:ServiceBus must be configured before using Service Bus.");
            }

            return new ServiceBusClient(connectionString);
        });

        return services;
    }

    public static IServiceCollection AddServiceBusPublisher(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddServiceBusClient(configuration);
        services.AddSingleton<ServiceBusSender>(serviceProvider =>
        {
            var client = serviceProvider.GetRequiredService<ServiceBusClient>();
            var options = serviceProvider.GetRequiredService<ServiceBusMessagingOptions>();
            return client.CreateSender(options.TopicName);
        });
        services.AddSingleton<IMessagePublisher, AzureServiceBusMessagePublisher>();

        return services;
    }

    public static IServiceCollection AddOutboxDispatcher(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = OutboxDispatcherOptions.FromConfiguration(configuration);
        services.AddSingleton(options);
        services.AddScoped<IOutboxMessageStore, EfOutboxMessageStore>();

        if (options.Enabled)
        {
            services.AddHostedService<OutboxDispatcherHostedService>();
        }

        return services;
    }
}
