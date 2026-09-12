# Validation: Add Observability

## Package and SDK gate

- Confirmed the repository-resolved `Azure.Messaging.ServiceBus` 7.20.2 package XML describes its ActivitySource instrumentation as experimental and feature-flag controlled.
- Registered the exact `Azure.Messaging.ServiceBus.Message` source.
- Added no `AppContext` switch; Service Bus transport spans require only the documented `AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE=true` environment opt-in.
- Added stable OpenTelemetry packages through Central Package Management and aligned `Npgsql.OpenTelemetry` with the existing Npgsql dependency line.

## Automated verification

- `dotnet restore CloudOrders.sln` completed successfully.
- `dotnet build CloudOrders.sln --no-restore` completed with zero warnings and zero errors.
- `dotnet test tests/CloudOrders.UnitTests/CloudOrders.UnitTests.csproj --no-restore` passed: 46 tests.
- `dotnet test tests/CloudOrders.IntegrationTests/CloudOrders.IntegrationTests.csproj --no-restore` passed: 10 tests.
- Focused tests verify application trace identifiers and worker trace/metric cardinality without Azure or an external telemetry exporter.

## Boundaries

- No live Azure namespace or external telemetry backend was used by automated validation.
- No dashboards, alerts, collector, Azure deployment configuration, retry behavior, settlement behavior, or message contracts were changed.
