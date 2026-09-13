using Microsoft.Extensions.Configuration;

namespace CloudOrders.Infrastructure.Messaging;

public sealed record OutboxDispatcherOptions(
    bool Enabled,
    int BatchSize,
    TimeSpan PollingInterval)
{
    public static OutboxDispatcherOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = ParseBoolean(configuration["Outbox:Enabled"], defaultValue: true, "Outbox:Enabled");
        var batchSize = ParsePositiveInteger(configuration["Outbox:BatchSize"], 20, "Outbox:BatchSize");
        var pollingIntervalSeconds = ParsePositiveInteger(
            configuration["Outbox:PollingIntervalSeconds"],
            5,
            "Outbox:PollingIntervalSeconds");

        return new OutboxDispatcherOptions(
            enabled,
            batchSize,
            TimeSpan.FromSeconds(pollingIntervalSeconds));
    }

    private static bool ParseBoolean(string? value, bool defaultValue, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{key} must be true or false.");
    }

    private static int ParsePositiveInteger(string? value, int defaultValue, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var parsed) || parsed < 1)
        {
            throw new InvalidOperationException($"{key} must be a positive integer.");
        }

        return parsed;
    }
}
