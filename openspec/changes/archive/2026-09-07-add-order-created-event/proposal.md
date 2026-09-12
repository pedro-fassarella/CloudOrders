# Add OrderCreated Event

## Objective

Publish a real `OrderCreated` integration event after a successful order persistence operation through the existing Azure Service Bus publisher and `order-events` topic.

## Motivation

The current messaging foundation proves Service Bus connectivity with a Development-only probe, while `POST /orders` still ends after persistence. This change connects the real order-creation use case to the established transport boundary without adding business consumers or production delivery infrastructure.

## Scope

- Public `OrderCreated` integration contract in Application, without exposing EF or Domain entities.
- Explicit domain-status-to-contract-status mapping.
- Publish from the create-order use case after persistence succeeds.
- Native Service Bus message ID, correlation ID, subject, and content type.
- Azure-independent unit and integration coverage using publisher test doubles.
- README documentation of the temporary persistence-plus-publish consistency limitation.
- Retention of the existing Development-only messaging probe and probe worker.

## Out of Scope

- Payment, inventory, and notification workers.
- Idempotency, retry, DLQ, Outbox, or recovery behavior.
- Observability expansion, Azure deployment, CI/CD, IaC, or Service Bus resource changes.
- Database schema changes, migrations, or a Contracts project.

## Success Criteria

1. A successful `POST /orders` persists the order and publishes one `OrderCreated` event to `order-events`.
2. The event has stable JSON order data and native Service Bus metadata with subject `CloudOrders.Orders.OrderCreated`.
3. A failed persistence operation does not attempt publishing.
4. A post-commit publishing failure is visible to the request and documented as a temporary delivery gap.
5. Automated tests do not require a live Azure Service Bus namespace.
