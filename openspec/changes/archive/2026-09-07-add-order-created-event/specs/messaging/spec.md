# Messaging Specification

## Requirement: Stable OrderCreated contract

The application MUST publish `OrderCreated` as a JSON integration contract containing only `orderId`, `customerId`, `status`, and `createdAtUtc`. It MUST NOT serialize an EF entity or Domain `Order` directly.

`status` MUST be a stable string contract value produced through an explicit mapping from the domain status; it MUST NOT be derived with `OrderStatus.ToString()`.

#### Scenario: Pending order event

- **WHEN** a Pending order is created
- **THEN** the event JSON has status `Pending`
- **AND** it contains the created order ID, customer ID, and UTC creation timestamp.

## Requirement: Native Service Bus metadata

`OrderCreated` MUST use the configured `order-events` topic and native Service Bus metadata with a generated `MessageId`, the order ID as `CorrelationId`, subject `CloudOrders.Orders.OrderCreated`, and content type `application/json`.

#### Scenario: Serialize an OrderCreated event

- **WHEN** the publisher creates a Service Bus message for `OrderCreated`
- **THEN** the JSON payload and all required native metadata are preserved
- **AND** the operation requires no Azure network connection.

## Requirement: Retain Development probe diagnostics

The Development-only probe endpoint and its filtered subscription MUST remain available for transport diagnostics.

#### Scenario: Publish an OrderCreated event while the probe worker runs

- **WHEN** an `OrderCreated` message is sent to `order-events`
- **THEN** the `messaging-probe` subscription filter excludes it
- **AND** the probe worker continues handling only `CloudOrders.Messaging.Probe` messages.
