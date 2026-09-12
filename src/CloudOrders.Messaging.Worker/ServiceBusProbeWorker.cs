using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Azure.Messaging.ServiceBus;
using CloudOrders.Application.Messaging;
using CloudOrders.Infrastructure.Messaging;

namespace CloudOrders.Messaging.Worker;

[SuppressMessage(
    "Performance",
    "CA1812",
    Justification = "The worker is instantiated by the hosted-service dependency injection registration.")]
internal sealed partial class ServiceBusProbeWorker(
    ServiceBusClient serviceBusClient,
    ServiceBusMessagingOptions options,
    ILogger<ServiceBusProbeWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processor = serviceBusClient.CreateProcessor(
            options.TopicName,
            options.SubscriptionName,
            new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = 1
            });

        await using (processor.ConfigureAwait(false))
        {
            processor.ProcessMessageAsync += ProcessMessageAsync;
            processor.ProcessErrorAsync += args =>
            {
                LogProcessorError(args.Exception, args.ErrorSource, args.EntityPath);
                return Task.CompletedTask;
            };

            await processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
            LogProcessorStarted(options.TopicName, options.SubscriptionName);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            finally
            {
                await processor.StopProcessingAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        if (!string.Equals(args.Message.Subject, MessagingProbe.Subject, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected message subject '{args.Message.Subject}'.");
        }

        if (!string.Equals(
                args.Message.ContentType,
                MessagingProbe.JsonContentType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unexpected content type '{args.Message.ContentType}'.");
        }

        var probe = args.Message.Body.ToObjectFromJson<MessagingProbe>(SerializerOptions)
            ?? throw new InvalidOperationException("The Service Bus probe body was empty.");

        LogProcessingProbe(
            args.Message.MessageId,
            args.Message.CorrelationId,
            args.Message.Subject,
            probe.Message);

        await args.CompleteMessageAsync(args.Message, args.CancellationToken).ConfigureAwait(false);

        LogCompletedProbe(
            args.Message.MessageId,
            args.Message.CorrelationId);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Service Bus probe processor started for topic {TopicName} and subscription {SubscriptionName}.")]
    private partial void LogProcessorStarted(string topicName, string subscriptionName);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Processing Service Bus probe {MessageId} with correlation {CorrelationId}, subject {Subject}, and message {Message}.")]
    private partial void LogProcessingProbe(
        string messageId,
        string? correlationId,
        string? subject,
        string message);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Completed Service Bus probe {MessageId} with correlation {CorrelationId}.")]
    private partial void LogCompletedProbe(string messageId, string? correlationId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Service Bus probe processor error from {ErrorSource} for {EntityPath}.")]
    private partial void LogProcessorError(
        Exception exception,
        ServiceBusErrorSource errorSource,
        string entityPath);
}
