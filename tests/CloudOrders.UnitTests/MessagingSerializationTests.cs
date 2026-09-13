using System.Text.Json;
using CloudOrders.Application.Messaging;
using CloudOrders.Infrastructure.Messaging;

namespace CloudOrders.UnitTests;

public sealed class MessagingSerializationTests
{
    [Fact]
    public void ServiceBusMessagePreservesNativeMetadataAndJsonPayload()
    {
        var probe = new MessagingProbe(
            "CloudOrders Azure Service Bus probe",
            new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        var metadata = new MessageMetadata(
            "message-123",
            "correlation-456",
            MessagingProbe.Subject,
            MessagingProbe.JsonContentType);

        var message = ServiceBusMessageFactory.Create(probe, metadata);
        var roundTrip = message.Body.ToObjectFromJson<MessagingProbe>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(metadata.MessageId, message.MessageId);
        Assert.Equal(metadata.CorrelationId, message.CorrelationId);
        Assert.Equal(metadata.Subject, message.Subject);
        Assert.Equal(metadata.ContentType, message.ContentType);
        Assert.Equal(probe, roundTrip);
    }

    [Fact]
    public void ServiceBusMessagePreservesOrderCreatedContractAndNativeMetadata()
    {
        var orderCreated = new OrderCreated(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "customer-123",
            OrderCreated.PendingStatus,
            new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        var metadata = new MessageMetadata(
            "message-123",
            orderCreated.OrderId.ToString("D"),
            OrderCreated.Subject,
            OrderCreated.JsonContentType);

        var message = ServiceBusMessageFactory.Create(orderCreated, metadata);
        var roundTrip = message.Body.ToObjectFromJson<OrderCreated>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(metadata.MessageId, message.MessageId);
        Assert.Equal(metadata.CorrelationId, message.CorrelationId);
        Assert.Equal(metadata.Subject, message.Subject);
        Assert.Equal(metadata.ContentType, message.ContentType);
        Assert.Equal(orderCreated, roundTrip);
    }

    [Fact]
    public void ServiceBusMessagePreservesOutboundMessagePayloadAndNativeMetadata()
    {
        var payload = "{\"orderId\":\"00000000-0000-0000-0000-000000000001\"}"u8.ToArray();
        var outbound = new OutboundMessage(
            "message-123",
            "correlation-456",
            OrderCreated.Subject,
            OrderCreated.JsonContentType,
            payload);

        var message = ServiceBusMessageFactory.Create(outbound);

        Assert.Equal(outbound.MessageId, message.MessageId);
        Assert.Equal(outbound.CorrelationId, message.CorrelationId);
        Assert.Equal(outbound.Subject, message.Subject);
        Assert.Equal(outbound.ContentType, message.ContentType);
        Assert.Equal(payload, message.Body.ToArray());
    }
}
