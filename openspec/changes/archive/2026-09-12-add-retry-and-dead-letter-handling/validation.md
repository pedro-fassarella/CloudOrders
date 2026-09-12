# Validation

## Completed checks

- `dotnet build CloudOrders.sln --no-restore` completed with zero warnings and zero errors.
- `dotnet test tests/CloudOrders.UnitTests --no-build --no-restore` passed 44 Azure-independent unit tests.
- `dotnet test tests/CloudOrders.IntegrationTests --no-build --no-restore` passed 10 PostgreSQL Testcontainers integration tests after Docker access was authorized.

## Final verification

- `dotnet build CloudOrders.sln --no-restore` completed again with zero warnings and zero errors.
- `dotnet test tests/CloudOrders.UnitTests --no-build --no-restore` passed 44 Azure-independent unit tests.
- `dotnet test tests/CloudOrders.IntegrationTests --no-build --no-restore` passed 10 PostgreSQL Testcontainers integration tests with local Docker access.
- The Azure smoke test confirmed that the `payment` subscription immediately dead-lettered a malformed `OrderCreated` with `MessageContractViolation`, without repeated deliveries.
- The Azure smoke test confirmed that a valid `OrderCreated` encountering a controlled PostgreSQL outage remained unsettled, was redelivered by Service Bus, and reached the subscription DLQ with `MaxDeliveryCountExceeded` at the configured `MaxDeliveryCount = 5`.
- Azure configuration for the business subscriptions was reviewed as `payment`, `inventory`, and `notification` with `MaxDeliveryCount = 5`; the `messaging-probe` subscription remains unchanged.
- Review confirmed durable `(ConsumerName, MessageId)` idempotency, safe completion of completed duplicates, and no Polly or application retry loop. No Service Bus access key, Azure client secret, or private key is present in the source or OpenSpec artifacts introduced by this change; the local `.env` file remains ignored. Existing local Docker PostgreSQL development configuration contains its non-production password separately and was not changed by this work.
- Canonical specs were promoted during finalization and the completed change was archived.
