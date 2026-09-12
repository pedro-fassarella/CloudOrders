using CloudOrders.Infrastructure.Messaging;
using CloudOrders.Messaging.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddServiceBusClient(builder.Configuration);
builder.Services.AddHostedService<ServiceBusProbeWorker>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
