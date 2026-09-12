# Add Notification Worker

## Objective

Introduce an independent Notification Worker that consumes `OrderCreated` integration events from Azure Service Bus and explicitly completes each message after a deterministic simulated notification succeeds.

## Scope

- Dedicated `CloudOrders.Notification.Worker` project using the existing `OrderCreated` contract and Service Bus infrastructure.
- Dedicated `notification` subscription on `order-events`, filtered to `CloudOrders.Orders.OrderCreated` only.
- Explicit Peek-Lock completion after successful contract processing and simulated notification.
- Structured successful-processing logs containing OrderId, MessageId, CorrelationId, notification channel, and notification reference.
- Azure-independent unit tests and documented simultaneous Payment/Inventory/Notification Azure smoke test.

## Out of Scope

- Real email or SMS providers, notification persistence, schema, database, or external HTTP calls.
- New integration events, idempotency, custom retry, DLQ handling, Outbox, observability expansion, deployment, CI/CD, and IaC.
- Payment or Inventory Worker changes and a shared generic worker framework.

## Success Criteria

1. An `OrderCreated` event is independently delivered to the filtered `notification` subscription.
2. The Notification Worker simulates a notification for `Pending`, logs its metadata, channel, and deterministic reference, then explicitly completes the message.
3. Contract failures and unsupported notification status failures occur before completion.
4. Automated tests do not require a live Azure namespace.
5. The README documents the combined Azure smoke flow without requiring it during implementation.
