using CloudOrders.Application.Messaging;
using CloudOrders.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudOrders.IntegrationTests;

public sealed class ProcessedMessageStoreTests(PostgreSqlFixture database) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task ExecuteOnceAsyncPersistsCompletedStateAcrossServiceProviders()
    {
        var messageId = Guid.NewGuid().ToString("N");

        using (var firstProvider = CreateServiceProvider())
        {
            var firstStore = firstProvider.GetRequiredService<IProcessedMessageStore>();
            var first = await firstStore.ExecuteOnceAsync(
                ConsumerIdentities.Payment,
                messageId,
                _ => Task.FromResult("payment-receipt"));

            Assert.False(first.WasAlreadyProcessed);
            Assert.Equal("payment-receipt", first.Result);
        }

        using var secondProvider = CreateServiceProvider();
        var secondStore = secondProvider.GetRequiredService<IProcessedMessageStore>();
        var businessCalls = 0;

        var second = await secondStore.ExecuteOnceAsync(
            ConsumerIdentities.Payment,
            messageId,
            _ =>
            {
                businessCalls++;
                return Task.FromResult("unexpected");
            });

        Assert.True(second.WasAlreadyProcessed);
        Assert.Null(second.Result);
        Assert.Equal(0, businessCalls);
    }

    [Fact]
    public async Task ExecuteOnceAsyncProcessesTheSameMessageIdOncePerConsumer()
    {
        using var provider = CreateServiceProvider();
        var store = provider.GetRequiredService<IProcessedMessageStore>();
        var messageId = Guid.NewGuid().ToString("N");
        var businessCalls = 0;

        foreach (var consumerName in new[]
                 {
                     ConsumerIdentities.Payment,
                     ConsumerIdentities.Inventory,
                     ConsumerIdentities.Notification
                 })
        {
            var execution = await store.ExecuteOnceAsync(
                consumerName,
                messageId,
                _ =>
                {
                    businessCalls++;
                    return Task.FromResult(consumerName);
                });

            Assert.False(execution.WasAlreadyProcessed);
        }

        foreach (var consumerName in new[]
                 {
                     ConsumerIdentities.Payment,
                     ConsumerIdentities.Inventory,
                     ConsumerIdentities.Notification
                 })
        {
            var duplicate = await store.ExecuteOnceAsync<string>(
                consumerName,
                messageId,
                _ => throw new InvalidOperationException("Duplicate callback must not run."));

            Assert.True(duplicate.WasAlreadyProcessed);
        }

        Assert.Equal(3, businessCalls);
    }

    [Fact]
    public async Task ExecuteOnceAsyncRollsBackWhenBusinessProcessingFails()
    {
        using var provider = CreateServiceProvider();
        var store = provider.GetRequiredService<IProcessedMessageStore>();
        var messageId = Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ExecuteOnceAsync(
                ConsumerIdentities.Inventory,
                messageId,
                _ => Task.FromException<string>(new InvalidOperationException("Simulated failure."))));

        var retriedBusinessCalls = 0;
        var retry = await store.ExecuteOnceAsync(
            ConsumerIdentities.Inventory,
            messageId,
            _ =>
            {
                retriedBusinessCalls++;
                return Task.FromResult("reserved");
            });

        Assert.False(retry.WasAlreadyProcessed);
        Assert.Equal(1, retriedBusinessCalls);
    }

    [Fact]
    public async Task ExecuteOnceAsyncConcurrentClaimsExecuteBusinessOperationOnlyOnce()
    {
        using var provider = CreateServiceProvider();
        var store = provider.GetRequiredService<IProcessedMessageStore>();
        var messageId = Guid.NewGuid().ToString("N");
        var firstOperationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var businessCalls = 0;

        var first = store.ExecuteOnceAsync(
            ConsumerIdentities.Notification,
            messageId,
            async cancellationToken =>
            {
                Interlocked.Increment(ref businessCalls);
                firstOperationStarted.SetResult();
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
                return "first";
            });

        await firstOperationStarted.Task;

        var second = store.ExecuteOnceAsync(
            ConsumerIdentities.Notification,
            messageId,
            _ =>
            {
                Interlocked.Increment(ref businessCalls);
                return Task.FromResult("second");
            });

        var results = await Task.WhenAll(first, second);

        Assert.False(results[0].WasAlreadyProcessed);
        Assert.True(results[1].WasAlreadyProcessed);
        Assert.Equal(1, businessCalls);
    }

    private ServiceProvider CreateServiceProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = database.ConnectionString
            })
            .Build();
        var services = new ServiceCollection();

        services.AddProcessedMessagePersistence(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }
}
