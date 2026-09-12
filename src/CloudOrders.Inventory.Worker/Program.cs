using CloudOrders.Infrastructure;
using CloudOrders.Infrastructure.Messaging;
using CloudOrders.Infrastructure.Observability;
using CloudOrders.Application.Observability;
using CloudOrders.Inventory.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddServiceBusClient(builder.Configuration);
builder.Services.AddProcessedMessagePersistence(builder.Configuration);
builder.Services.AddCloudOrdersObservability(
    builder.Configuration,
    builder.Environment,
    CloudOrdersTelemetry.InventoryWorkerServiceName);
builder.Services.AddSingleton<IInventoryProcessor, SimulatedInventoryProcessor>();
builder.Services.AddSingleton<InventoryMessageProcessor>();
builder.Services.AddHostedService<ServiceBusInventoryWorker>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
