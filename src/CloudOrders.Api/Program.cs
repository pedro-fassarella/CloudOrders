using CloudOrders.Application;
using CloudOrders.Application.Messaging;
using CloudOrders.Application.Orders;
using CloudOrders.Infrastructure;
using CloudOrders.Infrastructure.Messaging;
using CloudOrders.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<CloudOrdersDbContext>();
builder.Services.AddProblemDetails();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddServiceBusPublisher(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    app.MapPost("/messaging/probe", async (
            IMessagePublisher publisher,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var messageId = Guid.NewGuid().ToString();
            var correlationId = Guid.NewGuid().ToString();
            var probe = new MessagingProbe(
                "CloudOrders Azure Service Bus probe",
                timeProvider.GetUtcNow());
            var metadata = new MessageMetadata(
                messageId,
                correlationId,
                MessagingProbe.Subject,
                MessagingProbe.JsonContentType);

            await publisher.PublishAsync(probe, metadata, cancellationToken).ConfigureAwait(false);

            return Results.Accepted(value: new MessagingProbeResponse(
                messageId,
                correlationId,
                MessagingProbe.Subject));
        })
        .WithName("PublishMessagingProbe")
        .WithTags("Messaging");
}

app.MapHealthChecks("/health")
    .WithTags("Health");

app.MapPost("/orders", async (
        CreateOrderRequest request,
        CreateOrderHandler handler,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.CustomerId))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.CustomerId)] = ["CustomerId is required."]
            });
        }

        try
        {
            var created = await handler.HandleAsync(
                new CreateOrderCommand(request.CustomerId),
                cancellationToken).ConfigureAwait(false);

            var response = new OrderResponse(
                created.Id,
                created.CustomerId,
                created.Status.ToString(),
                created.CreatedAtUtc);

            return Results.Created($"/orders/{created.Id}", response);
        }
        catch (ArgumentException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.CustomerId)] = [exception.Message]
            });
        }
    })
    .WithName("CreateOrder")
    .WithTags("Orders");

app.MapGet("/orders/{id}", async (
        Guid id,
        GetOrderHandler handler,
        CancellationToken cancellationToken) =>
    {
        var order = await handler.HandleAsync(id, cancellationToken).ConfigureAwait(false);

        return order is null
            ? Results.NotFound()
            : Results.Ok(new OrderResponse(
                order.Id,
                order.CustomerId,
                order.Status.ToString(),
                order.CreatedAtUtc));
    })
    .WithName("GetOrderById")
    .WithTags("Orders");

await app.RunAsync().ConfigureAwait(false);

internal sealed record CreateOrderRequest(string? CustomerId);

internal sealed record OrderResponse(
    Guid Id,
    string CustomerId,
    string Status,
    DateTimeOffset CreatedAtUtc);

internal sealed record MessagingProbeResponse(
    string MessageId,
    string CorrelationId,
    string Subject);

internal partial class Program;
