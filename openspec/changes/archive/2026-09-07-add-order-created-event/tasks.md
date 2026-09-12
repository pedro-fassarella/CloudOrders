# Implementation Tasks

## Planning gate

- [x] Create and review the proposal, design, order and messaging specifications, and implementation tasks.
- [x] Confirm the explicit domain-status-to-contract-status mapping and the temporary consistency tradeoff.

## Implementation

- [x] Add the Application `OrderCreated` contract and stable metadata constants.
- [x] Update `CreateOrderHandler` to persist, explicitly map status, then publish through `IMessagePublisher`.
- [x] Preserve the Service Bus publisher, `order-events` topic configuration, and Development probe path.
- [x] Add Azure-independent unit and serialization tests.
- [x] Replace the Azure publisher with an in-memory publisher in the integration test host and cover the HTTP creation flow.
- [x] Update README documentation for the event flow, probe scope, and temporary persistence-plus-publish failure behavior.

## Verification

- [x] Restore and build the solution.
- [x] Run unit tests without Service Bus configuration.
- [x] Run PostgreSQL Testcontainers integration tests without a live Service Bus namespace.
- [x] Confirm no Outbox, retry/DLQ, idempotency, worker, Azure deployment, CI/CD, IaC, migration, or automatic archive was added.
