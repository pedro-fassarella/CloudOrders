# Messaging Specification

## Requirement: Notification subscription isolation

The `order-events` topic MUST have a dedicated `notification` subscription. Its `$Default` rule MUST be removed and it MUST use a SQL rule equivalent to `sys.Label = 'CloudOrders.Orders.OrderCreated'`.

#### Scenario: Fan out an OrderCreated event

- **WHEN** the API publishes an event with subject `CloudOrders.Orders.OrderCreated`
- **THEN** payment, inventory, and notification each receive an independent copy
- **AND** the messaging-probe subscription does not receive that event.

## Requirement: Explicit notification-message settlement

The Notification Worker MUST use Peek-Lock processing with automatic completion disabled and MUST explicitly complete a message only after successful contract processing and simulated notification.

#### Scenario: Notification contract or business failure

- **WHEN** a received message has an unexpected subject or content type, an empty or invalid `OrderCreated` body, or an unsupported order status
- **THEN** it MUST NOT be completed
- **AND** the change MUST NOT define custom retry, abandon, or dead-letter behavior.

## Requirement: Azure-independent Notification tests

Automated Notification Worker tests MUST not require a live Azure Service Bus namespace.

#### Scenario: Run Notification tests without Service Bus configuration

- **WHEN** normal tests run without `ConnectionStrings:ServiceBus`
- **THEN** Notification contract-processing and settlement-order tests execute through local seams only.
