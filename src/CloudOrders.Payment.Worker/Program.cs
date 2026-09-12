using CloudOrders.Infrastructure;
using CloudOrders.Infrastructure.Messaging;
using CloudOrders.Infrastructure.Observability;
using CloudOrders.Application.Observability;
using CloudOrders.Payment.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddServiceBusClient(builder.Configuration);
builder.Services.AddProcessedMessagePersistence(builder.Configuration);
builder.Services.AddCloudOrdersObservability(
    builder.Configuration,
    builder.Environment,
    CloudOrdersTelemetry.PaymentWorkerServiceName);
builder.Services.AddSingleton<IPaymentProcessor, SimulatedPaymentProcessor>();
builder.Services.AddSingleton<PaymentMessageProcessor>();
builder.Services.AddHostedService<ServiceBusPaymentWorker>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
