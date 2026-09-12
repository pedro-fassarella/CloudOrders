using CloudOrders.Infrastructure;
using CloudOrders.Infrastructure.Messaging;
using CloudOrders.Infrastructure.Observability;
using CloudOrders.Application.Observability;
using CloudOrders.Notification.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddServiceBusClient(builder.Configuration);
builder.Services.AddProcessedMessagePersistence(builder.Configuration);
builder.Services.AddCloudOrdersObservability(
    builder.Configuration,
    builder.Environment,
    CloudOrdersTelemetry.NotificationWorkerServiceName);
builder.Services.AddSingleton<INotificationProcessor, SimulatedNotificationProcessor>();
builder.Services.AddSingleton<NotificationMessageProcessor>();
builder.Services.AddHostedService<ServiceBusNotificationWorker>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
