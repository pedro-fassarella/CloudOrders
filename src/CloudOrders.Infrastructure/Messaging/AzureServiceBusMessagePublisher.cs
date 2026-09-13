using Azure.Messaging.ServiceBus;
using CloudOrders.Application.Messaging;

namespace CloudOrders.Infrastructure.Messaging;

internal sealed class AzureServiceBusMessagePublisher(ServiceBusSender sender) : IMessagePublisher
{
    public Task PublishAsync(
        OutboundMessage message,
        CancellationToken cancellationToken = default)
    {
        return sender.SendMessageAsync(
            ServiceBusMessageFactory.Create(message),
            cancellationToken);
    }

    public Task PublishAsync<TPayload>(
        TPayload payload,
        MessageMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        var message = ServiceBusMessageFactory.Create(payload, metadata);
        return sender.SendMessageAsync(message, cancellationToken);
    }
}
