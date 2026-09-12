using Microsoft.Extensions.Configuration;

namespace CloudOrders.Infrastructure.Messaging;

public sealed record ServiceBusMessagingOptions(string TopicName, string SubscriptionName)
{
    public static ServiceBusMessagingOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var topicName = configuration["Messaging:TopicName"];
        var subscriptionName = configuration["Messaging:SubscriptionName"];

        if (string.IsNullOrWhiteSpace(topicName))
        {
            throw new InvalidOperationException("Messaging:TopicName must be configured before using Service Bus.");
        }

        if (string.IsNullOrWhiteSpace(subscriptionName))
        {
            throw new InvalidOperationException(
                "Messaging:SubscriptionName must be configured before using Service Bus.");
        }

        return new ServiceBusMessagingOptions(topicName, subscriptionName);
    }
}
