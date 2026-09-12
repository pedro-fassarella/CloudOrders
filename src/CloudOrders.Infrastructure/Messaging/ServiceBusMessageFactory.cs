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
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.MessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.ContentType);

        var body = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);

        return new ServiceBusMessage(BinaryData.FromBytes(body))
        {
            MessageId = metadata.MessageId,
            CorrelationId = metadata.CorrelationId,
            Subject = metadata.Subject,
            ContentType = metadata.ContentType
        };
    }
}
