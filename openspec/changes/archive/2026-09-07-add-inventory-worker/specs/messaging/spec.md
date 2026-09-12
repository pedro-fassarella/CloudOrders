# Messaging Specification

## Requirement: Inventory subscription isolation

The `order-events` topic MUST have a dedicated `inventory` subscription. Its `$Default` rule MUST be removed and it MUST use a SQL rule equivalent to `sys.Label = 'CloudOrders.Orders.OrderCreated'`.

#### Scenario: Publish an OrderCreated event

- **WHEN** the API publishes an event with subject `CloudOrders.Orders.OrderCreated`
- **THEN** the inventory subscription receives a copy
- **AND** the payment subscription receives its independent copy
- **AND** the messaging-probe subscription does not receive either copy.

## Requirement: Explicit inventory-message settlement

The Inventory Worker MUST use Peek-Lock processing with automatic completion disabled and MUST explicitly complete a message only after successful contract processing and simulated reservation.

#### Scenario: Contract or business failure

- **WHEN** a received message has an unexpected subject or content type, an empty or invalid `OrderCreated` body, or an unsupported order status
- **THEN** it MUST NOT be completed
- **AND** the change MUST NOT define custom retry, abandon, or dead-letter behavior.

## Requirement: Azure-independent tests

Automated Inventory Worker tests MUST not require a live Azure Service Bus namespace.

#### Scenario: Run tests without Service Bus configuration

- **WHEN** normal tests run without `ConnectionStrings:ServiceBus`
- **THEN** contract-processing and settlement-order tests execute through local seams only.
