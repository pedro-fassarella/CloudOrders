# Add Outbox Pattern

## Objective

Replace direct `OrderCreated` publication after order persistence with a transactional PostgreSQL outbox. `POST /orders` must commit the Order and its durable outbox record together, while an asynchronous dispatcher publishes the stored event after commit.

## Scope

- Persist `OrderCreated`, native Service Bus metadata, stable MessageId, and optional W3C trace metadata in PostgreSQL with the Order.
- Add a configurable API-hosted polling dispatcher that publishes pending rows and marks them published only after Service Bus confirms the send.
- Preserve the current `OrderCreated` JSON contract, topic routing metadata, Development probe path, and idempotent business consumers.
- Add Azure-independent unit and PostgreSQL Testcontainers coverage.
- Document that initial deployment supports one active dispatcher instance only.

## Non-goals

- Multi-instance dispatcher claims, leases, PostgreSQL row locking, or `FOR UPDATE SKIP LOCKED`.
- Outbox cleanup, retention, replay, retry limits, poison handling, or broker duplicate detection.
- New events, business behavior, Azure deployment, IaC, CI/CD, or request idempotency.

## Success criteria

1. Order and outbox row commit or roll back together.
2. The API does not publish directly while creating an order.
3. Dispatcher retries pending rows with the original MessageId and metadata.
4. Send-success/mark-published failure can duplicate safely through existing idempotent consumers.
5. Automated tests require no live Azure namespace.

## Deployment constraint

The initial deployment MUST have exactly one active Outbox Dispatcher. Stable MessageIds and idempotent consumers make duplicate business processing safe, but they do not coordinate concurrent dispatcher instances.
