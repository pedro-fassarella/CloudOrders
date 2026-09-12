# Messaging Specification

## Requirement: Payment subscription isolation

The `order-events` topic MUST have a dedicated `payment` subscription. Its `$Default` rule MUST be removed and it MUST use a SQL rule equivalent to `sys.Label = 'CloudOrders.Orders.OrderCreated'`.

#### Scenario: Publish an OrderCreated event

- **WHEN** the API publishes an event with subject `CloudOrders.Orders.OrderCreated`
- **THEN** the payment subscription receives a copy
- **AND** the existing `messaging-probe` subscription does not receive that copy.

## Requirement: Explicit payment-message settlement

The Payment Worker MUST use Peek-Lock processing with auto-completion disabled and MUST explicitly complete a message only after successful message processing.

#### Scenario: Contract failure

- **WHEN** a received message has an unexpected subject or content type, or cannot be deserialized as `OrderCreated`
- **THEN** it MUST NOT be completed
- **AND** the change MUST NOT define custom retry, abandon, or dead-letter behavior.

## Requirement: Azure-independent tests

Automated tests for payment message processing MUST not require a live Azure Service Bus namespace.

#### Scenario: Run tests without Service Bus configuration

- **WHEN** normal tests run without `ConnectionStrings:ServiceBus`
- **THEN** contract-processing and settlement-order tests execute through local seams only.
