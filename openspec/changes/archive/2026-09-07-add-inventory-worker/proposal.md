# Add Inventory Worker

## Objective

Introduce an independent Inventory Worker that consumes `OrderCreated` integration events from Azure Service Bus and explicitly completes each message after a deterministic simulated inventory reservation succeeds.

## Scope

- Dedicated `CloudOrders.Inventory.Worker` project using the existing `OrderCreated` contract and Service Bus infrastructure.
- Dedicated `inventory` subscription on `order-events`, filtered to `CloudOrders.Orders.OrderCreated` only.
- Explicit Peek-Lock completion after successful contract processing and simulated reservation.
- Structured successful-processing logs containing OrderId, MessageId, CorrelationId, and reservation reference.
- Azure-independent unit tests and documented simultaneous Payment/Inventory Azure smoke test.

## Out of Scope

- Notification Worker, real inventory system, inventory persistence, schema, or database.
- New integration events, idempotency, custom retry, DLQ handling, Outbox, observability expansion, deployment, CI/CD, and IaC.

## Success Criteria

1. An `OrderCreated` event is independently delivered to the filtered `inventory` subscription.
2. The Inventory Worker simulates a reservation for `Pending`, logs its metadata and deterministic reference, then explicitly completes the message.
3. Contract failures and unsupported inventory status failures occur before completion.
4. Automated tests do not require a live Azure namespace.
5. The documented smoke test confirms independent fan-out to running Payment and Inventory Workers.
