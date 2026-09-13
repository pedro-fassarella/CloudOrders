using System.Text.Json;
using Azure.Messaging.ServiceBus;
using CloudOrders.Application.Messaging;

namespace CloudOrders.Infrastructure.Messaging;

internal static class ServiceBusMessageFactory
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    internal static ServiceBusMessage Create<TPayload>(TPayload payload, MessageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var body = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);

        return Create(new OutboundMessage(
            metadata.MessageId,
            metadata.CorrelationId,
            metadata.Subject,
            metadata.ContentType,
            body));
    }

    internal static ServiceBusMessage Create(OutboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.MessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.ContentType);

        return new ServiceBusMessage(BinaryData.FromBytes(message.Payload))
        {
            MessageId = message.MessageId,
            CorrelationId = message.CorrelationId,
            Subject = message.Subject,
            ContentType = message.ContentType
        };
    }
}
