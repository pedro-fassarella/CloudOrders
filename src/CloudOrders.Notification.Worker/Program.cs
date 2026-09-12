using CloudOrders.Infrastructure;
using CloudOrders.Infrastructure.Messaging;
using CloudOrders.Notification.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddServiceBusClient(builder.Configuration);
builder.Services.AddProcessedMessagePersistence(builder.Configuration);
builder.Services.AddSingleton<INotificationProcessor, SimulatedNotificationProcessor>();
builder.Services.AddSingleton<NotificationMessageProcessor>();
builder.Services.AddHostedService<ServiceBusNotificationWorker>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
