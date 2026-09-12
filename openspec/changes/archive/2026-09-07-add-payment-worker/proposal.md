# Add Payment Worker

## Objective

Introduce an independent Payment Worker that consumes `OrderCreated` integration events from Azure Service Bus and completes each message after a deterministic simulated payment succeeds.

## Scope

- Dedicated `CloudOrders.Payment.Worker` project using the current `OrderCreated` contract and Service Bus infrastructure.
- Dedicated `payment` subscription on `order-events`, with a rule for `CloudOrders.Orders.OrderCreated` only.
- Explicit Peek-Lock completion after successful contract processing and simulated payment.
- Structured logs containing OrderId, MessageId, and CorrelationId.
- Azure-independent unit tests and a documented manual Azure smoke test.

## Out of Scope

- Real payment gateway, payment persistence, schema, or database.
- Inventory and notification workers.
- Idempotency, custom retry, DLQ handling, Outbox, observability expansion, deployment, CI/CD, and IaC.

## Success Criteria

1. A successful `POST /orders` produces an `OrderCreated` message received by the `payment` subscription only.
2. The Payment Worker deserializes the existing JSON contract, simulates payment for `Pending`, logs metadata, and explicitly completes the message.
3. Message-contract failures and unsupported payment status failures occur before completion.
4. Normal automated tests require no live Azure namespace.
