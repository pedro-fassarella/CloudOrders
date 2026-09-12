using System.Diagnostics.CodeAnalysis;
using Azure.Messaging.ServiceBus;
using CloudOrders.Application.Messaging;
using CloudOrders.Application.Observability;
using CloudOrders.Infrastructure.Messaging;

namespace CloudOrders.Inventory.Worker;

[SuppressMessage(
    "Performance",
    "CA1812",
    Justification = "The worker is instantiated by the hosted-service dependency injection registration.")]
internal sealed partial class ServiceBusInventoryWorker(
    ServiceBusClient serviceBusClient,
    ServiceBusMessagingOptions options,
    InventoryMessageProcessor inventoryMessageProcessor,
    ILogger<ServiceBusInventoryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processor = serviceBusClient.CreateProcessor(
            options.TopicName,
            options.SubscriptionName,
            new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = 1,
                ReceiveMode = ServiceBusReceiveMode.PeekLock
            });

        await using (processor.ConfigureAwait(false))
        {
            using var scope = CloudOrdersTelemetry.BeginProcessorScope(
                logger,
                CloudOrdersTelemetry.InventoryWorkerServiceName,
                ConsumerIdentities.Inventory);
            processor.ProcessMessageAsync += ProcessMessageAsync;
            processor.ProcessErrorAsync += args =>
            {
                using var errorScope = CloudOrdersTelemetry.BeginProcessorScope(
                    logger,
                    CloudOrdersTelemetry.InventoryWorkerServiceName,
                    ConsumerIdentities.Inventory);
                CloudOrdersTelemetry.RecordProcessorError(
                    ConsumerIdentities.Inventory,
                    args.ErrorSource.ToString());
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

    private Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        return inventoryMessageProcessor.ProcessAsync(
            args.Message.Body.ToArray(),
            args.Message.MessageId,
            args.Message.CorrelationId,
            args.Message.Subject,
            args.Message.ContentType,
            cancellationToken => args.CompleteMessageAsync(args.Message, cancellationToken),
            (details, cancellationToken) => args.DeadLetterMessageAsync(
                args.Message,
                details.Reason,
                details.Description,
                cancellationToken),
            args.CancellationToken,
            args.Message.DeliveryCount);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Inventory processor started for topic {TopicName} and subscription {SubscriptionName}.")]
    private partial void LogProcessorStarted(string topicName, string subscriptionName);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Inventory processor error from {ErrorSource} for {EntityPath}.")]
    private partial void LogProcessorError(
        Exception exception,
        ServiceBusErrorSource errorSource,
        string entityPath);
}
