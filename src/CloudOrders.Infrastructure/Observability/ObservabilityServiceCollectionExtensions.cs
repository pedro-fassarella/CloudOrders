using CloudOrders.Application.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CloudOrders.Infrastructure.Observability;

public static class ObservabilityServiceCollectionExtensions
{
    private const string ServiceBusActivitySourceName = "Azure.Messaging.ServiceBus.Message";

    public static IServiceCollection AddCloudOrdersObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string serviceName,
        bool includeAspNetCoreInstrumentation = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var exporter = ResolveExporter(configuration, environment);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(CloudOrdersTelemetry.ActivitySourceName)
                    .AddSource(ServiceBusActivitySourceName)
                    .AddNpgsql();

                if (includeAspNetCoreInstrumentation)
                {
                    tracing.AddAspNetCoreInstrumentation();
                }

                ConfigureTracingExporter(tracing, exporter);
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(CloudOrdersTelemetry.MeterName)
                    .AddMeter("Microsoft.EntityFrameworkCore")
                    .AddNpgsqlInstrumentation()
                    .AddRuntimeInstrumentation();

                if (includeAspNetCoreInstrumentation)
                {
                    metrics.AddAspNetCoreInstrumentation();
                }

                ConfigureMetricsExporter(metrics, exporter);
            });

        return services;
    }

    private static string ResolveExporter(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Observability:Exporter"];
        var exporter = string.IsNullOrWhiteSpace(configured)
            ? environment.IsDevelopment() ? "Console" : "None"
            : configured;

        return exporter.ToLowerInvariant() switch
        {
            "console" => "Console",
            "otlp" => "Otlp",
            "none" => "None",
            _ => throw new InvalidOperationException(
                "Observability:Exporter must be Console, Otlp, or None.")
        };
    }

    private static void ConfigureTracingExporter(TracerProviderBuilder tracing, string exporter)
    {
        if (string.Equals(exporter, "Console", StringComparison.Ordinal))
        {
            tracing.AddConsoleExporter();
        }
        else if (string.Equals(exporter, "Otlp", StringComparison.Ordinal))
        {
            tracing.AddOtlpExporter();
        }
    }

    private static void ConfigureMetricsExporter(MeterProviderBuilder metrics, string exporter)
    {
        if (string.Equals(exporter, "Console", StringComparison.Ordinal))
        {
            metrics.AddConsoleExporter();
        }
        else if (string.Equals(exporter, "Otlp", StringComparison.Ordinal))
        {
            metrics.AddOtlpExporter();
        }
    }
}
