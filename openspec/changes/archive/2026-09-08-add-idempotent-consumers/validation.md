# Validation

## Completed checks

- `dotnet restore CloudOrders.sln` completed successfully.
- `dotnet build CloudOrders.sln --no-restore` completed with zero warnings and zero errors.
- `dotnet ef migrations has-pending-model-changes --project src/CloudOrders.Infrastructure --startup-project src/CloudOrders.Api --no-build` reported no pending model changes when supplied a local-only design-time PostgreSQL connection string.
- The PostgreSQL Testcontainers fixture applied the `AddProcessedMessages` migration during integration tests.
- `dotnet test tests/CloudOrders.UnitTests --no-build --no-restore` passed 38 tests without Service Bus configuration.
- `dotnet test tests/CloudOrders.IntegrationTests --no-build --no-restore` passed 10 tests without a live Azure namespace, including durable state, consumer isolation, rollback, and concurrent claim coverage.

## Notes

- The first Testcontainers attempt could not access Docker from the sandbox. The suite passed after Docker access was authorized; no test uses a live Azure Service Bus namespace.
- EF tools emitted the existing informational warning that tool version `10.0.5` is older than runtime `10.0.11`; migration generation and model validation succeeded.
- This change remains unarchived by design.
