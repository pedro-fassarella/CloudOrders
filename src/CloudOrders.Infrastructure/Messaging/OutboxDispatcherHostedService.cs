using System.Diagnostics.CodeAnalysis;
using CloudOrders.Application.Messaging;
using CloudOrders.Application.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudOrders.Infrastructure.Messaging;

[SuppressMessage(
    "Performance",
    "CA1812",
    Justification = "The dispatcher is instantiated by the hosted-service dependency injection registration.")]
internal sealed partial class OutboxDispatcherHostedService(
    IServiceScopeFactory serviceScopeFactory,
    IMessagePublisher publisher,
    OutboxDispatcherOptions options,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcherHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(options.BatchSize, options.PollingInterval);

        await DispatchBatchAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(options.PollingInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await DispatchBatchAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task DispatchBatchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IOutboxMessageStore>();
            var pending = await store.GetPendingAsync(options.BatchSize, cancellationToken).ConfigureAwait(false);

            foreach (var outboxMessage in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DispatchAsync(store, outboxMessage, cancellationToken).ConfigureAwait(false);
            }

            CloudOrdersTelemetry.SetOutboxPendingCount(
                await store.CountPendingAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogPollingFailed(exception, exception.GetType().Name);
        }
    }

    private async Task DispatchAsync(
        IOutboxMessageStore store,
        PendingOutboxMessage outboxMessage,
        CancellationToken cancellationToken)
    {
        var metadata = outboxMessage.Message;
        using var activity = CloudOrdersTelemetry.StartOutboxDispatch(
            outboxMessage.OrderId,
            metadata.MessageId,
            metadata.CorrelationId,
            outboxMessage.TraceContext);
        using var scope = CloudOrdersTelemetry.BeginOutboxScope(
            logger,
            CloudOrdersTelemetry.ApiServiceName,
            outboxMessage.OrderId,
            metadata.MessageId,
            metadata.CorrelationId);
        var attemptedAtUtc = timeProvider.GetUtcNow();

        try
        {
            LogDispatching();
            await publisher.PublishAsync(metadata, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                await store.RecordFailedAttemptAsync(
                        outboxMessage.Id,
                        attemptedAtUtc,
                        exception.GetType().Name,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception persistenceException) when (!cancellationToken.IsCancellationRequested)
            {
                LogFailureRecordingFailed(persistenceException, persistenceException.GetType().Name);
            }

            CloudOrdersTelemetry.RecordOutboxDispatchAttempt("failed");
            CloudOrdersTelemetry.SetFailure(activity, exception);
            LogPublishingFailed(exception, exception.GetType().Name);
            return;
        }

        try
        {
            await store.MarkPublishedAsync(outboxMessage.Id, attemptedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            CloudOrdersTelemetry.RecordOutboxDispatchAttempt("failed");
            CloudOrdersTelemetry.SetFailure(activity, exception);
            LogMarkPublishedFailed(exception, exception.GetType().Name);
            return;
        }

        CloudOrdersTelemetry.RecordOrderPublished();
        CloudOrdersTelemetry.RecordOutboxDispatchAttempt("published");
        CloudOrdersTelemetry.SetOutcome(activity, "published");
        LogPublished();
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Outbox dispatcher started with batch size {BatchSize} and polling interval {PollingInterval}.")]
    private partial void LogStarted(int batchSize, TimeSpan pollingInterval);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dispatching pending OrderCreated outbox message.")]
    private partial void LogDispatching();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Published OrderCreated outbox message and marked it as published.")]
    private partial void LogPublished();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "OrderCreated outbox publication failed with {FailureType}; the message remains pending.")]
    private partial void LogPublishingFailed(Exception exception, string failureType);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "OrderCreated outbox publication succeeded but marking it published failed with {FailureType}; duplicate delivery is possible.")]
    private partial void LogMarkPublishedFailed(Exception exception, string failureType);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Recording an OrderCreated outbox publication failure failed with {FailureType}.")]
    private partial void LogFailureRecordingFailed(Exception exception, string failureType);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Outbox polling cycle failed with {FailureType}.")]
    private partial void LogPollingFailed(Exception exception, string failureType);
}
