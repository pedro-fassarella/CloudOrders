using System.Net;
using System.Net.Http.Json;
using CloudOrders.Application.Messaging;
using CloudOrders.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CloudOrders.IntegrationTests;

public sealed class OrdersApiTests(PostgreSqlFixture database) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task HealthReturnsOkWhenPostgresIsAvailable()
    {
        using var factory = new IntegrationTestFactory(database.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task InvalidOrderReturnsProblemDetails()
    {
        using var factory = new IntegrationTestFactory(database.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(new Uri("/orders", UriKind.Relative), new { customerId = " " }).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task CreatedOrderIsPersistedAndCanBeRetrieved()
    {
        using var factory = new IntegrationTestFactory(database.ConnectionString);
        using var client = factory.CreateClient();

        var createResponse = await client.PostAsJsonAsync(new Uri("/orders", UriKind.Relative), new { customerId = "customer-123" }).ConfigureAwait(true);
        var created = await createResponse.Content.ReadFromJsonAsync<OrderResponse>().ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.NotNull(created);
        Assert.Equal($"/orders/{created!.Id}", createResponse.Headers.Location?.OriginalString);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudOrdersDbContext>();
        var persisted = await db.Orders.SingleAsync(order => order.Id == created.Id).ConfigureAwait(true);
        Assert.Equal("customer-123", persisted.CustomerId);

        var getResponse = await client.GetAsync(new Uri($"/orders/{created.Id}", UriKind.Relative)).ConfigureAwait(true);
        var retrieved = await getResponse.Content.ReadFromJsonAsync<OrderResponse>().ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.NotNull(retrieved);
        Assert.Equal(created.Id, retrieved!.Id);
        Assert.Equal(created.CustomerId, retrieved.CustomerId);
        Assert.Equal(created.Status, retrieved.Status);
        Assert.Equal(
            created.CreatedAtUtc.ToUnixTimeMilliseconds(),
            retrieved.CreatedAtUtc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task CreatedOrderPublishesOrderCreatedEvent()
    {
        using var factory = new IntegrationTestFactory(database.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/orders", UriKind.Relative),
            new { customerId = "customer-123" }).ConfigureAwait(true);
        var created = await response.Content.ReadFromJsonAsync<OrderResponse>().ConfigureAwait(true);
        var publisher = factory.Services.GetRequiredService<CapturingMessagePublisher>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(created);

        var published = Assert.IsType<OrderCreated>(publisher.Payload);
        var metadata = Assert.IsType<MessageMetadata>(publisher.Metadata);
        Assert.Equal(created!.Id, published.OrderId);
        Assert.Equal(created.CustomerId, published.CustomerId);
        Assert.Equal(OrderCreated.PendingStatus, published.Status);
        Assert.Equal(created.CreatedAtUtc.ToUnixTimeMilliseconds(), published.CreatedAtUtc.ToUnixTimeMilliseconds());
        Assert.True(Guid.TryParse(metadata.MessageId, out _));
        Assert.Equal(created.Id.ToString("D"), metadata.CorrelationId);
        Assert.Equal(OrderCreated.Subject, metadata.Subject);
        Assert.Equal(OrderCreated.JsonContentType, metadata.ContentType);
    }

    [Fact]
    public async Task UnknownOrderReturnsNotFound()
    {
        using var factory = new IntegrationTestFactory(database.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri($"/orders/{Guid.NewGuid()}", UriKind.Relative)).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task InvalidOrderIdUsesStandardGuidBindingResponse()
    {
        using var factory = new IntegrationTestFactory(database.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/orders/not-a-guid", UriKind.Relative)).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

internal sealed class IntegrationTestFactory(string connectionString)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMessagePublisher>();
            services.AddSingleton<CapturingMessagePublisher>();
            services.AddSingleton<IMessagePublisher>(serviceProvider =>
                serviceProvider.GetRequiredService<CapturingMessagePublisher>());
        });
    }
}

internal sealed class CapturingMessagePublisher : IMessagePublisher
{
    public object? Payload { get; private set; }

    public MessageMetadata? Metadata { get; private set; }

    public Task PublishAsync<TPayload>(
        TPayload payload,
        MessageMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        Payload = payload;
        Metadata = metadata;
        return Task.CompletedTask;
    }
}
