using CloudOrders.Infrastructure;
using CloudOrders.Infrastructure.Messaging;
using CloudOrders.Inventory.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddServiceBusClient(builder.Configuration);
builder.Services.AddProcessedMessagePersistence(builder.Configuration);
builder.Services.AddSingleton<IInventoryProcessor, SimulatedInventoryProcessor>();
builder.Services.AddSingleton<InventoryMessageProcessor>();
builder.Services.AddHostedService<ServiceBusInventoryWorker>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
