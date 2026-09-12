using CloudOrders.Infrastructure;
using CloudOrders.Infrastructure.Messaging;
using CloudOrders.Payment.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddServiceBusClient(builder.Configuration);
builder.Services.AddProcessedMessagePersistence(builder.Configuration);
builder.Services.AddSingleton<IPaymentProcessor, SimulatedPaymentProcessor>();
builder.Services.AddSingleton<PaymentMessageProcessor>();
builder.Services.AddHostedService<ServiceBusPaymentWorker>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
