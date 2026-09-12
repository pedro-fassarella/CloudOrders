namespace CloudOrders.Application.Messaging;

public sealed record MessagingProbe(string Message, DateTimeOffset SentAtUtc)
{
    public const string Subject = "CloudOrders.Messaging.Probe";

    public const string JsonContentType = "application/json";
}
