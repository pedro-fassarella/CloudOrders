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
- **AND** it MUST be immediately dead-lettered with reason `MessageContractViolation`.

## Requirement: Azure-independent tests

Automated tests for payment message processing MUST not require a live Azure Service Bus namespace.

#### Scenario: Run tests without Service Bus configuration

- **WHEN** normal tests run without `ConnectionStrings:ServiceBus`
- **THEN** contract-processing and settlement-order tests execute through local seams only.

## Requirement: Inventory subscription isolation

The `order-events` topic MUST have a dedicated `inventory` subscription. Its `$Default` rule MUST be removed and it MUST use a SQL rule equivalent to `sys.Label = 'CloudOrders.Orders.OrderCreated'`.

#### Scenario: Fan out an OrderCreated event

- **WHEN** the API publishes an event with subject `CloudOrders.Orders.OrderCreated`
- **THEN** payment and inventory each receive an independent copy
- **AND** the messaging-probe subscription does not receive that event.

## Requirement: Explicit inventory-message settlement

The Inventory Worker MUST use Peek-Lock processing with auto-completion disabled and MUST explicitly complete a message only after successful contract processing and simulated reservation.

#### Scenario: Inventory contract or business failure

- **WHEN** a received message has an unexpected subject or content type, an empty or invalid `OrderCreated` body, or an unsupported order status
- **THEN** it MUST NOT be completed
- **AND** a contract failure MUST be immediately dead-lettered with reason `MessageContractViolation`
- **AND** an unsupported status MUST be immediately dead-lettered with reason `UnsupportedBusinessStatus` after its inbox transaction rolls back.

## Requirement: Azure-independent Inventory tests

Automated Inventory Worker tests MUST not require a live Azure Service Bus namespace.

#### Scenario: Run Inventory tests without Service Bus configuration

- **WHEN** normal tests run without `ConnectionStrings:ServiceBus`
- **THEN** Inventory contract-processing and settlement-order tests execute through local seams only.

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
- **AND** a contract failure MUST be immediately dead-lettered with reason `MessageContractViolation`
- **AND** an unsupported status MUST be immediately dead-lettered with reason `UnsupportedBusinessStatus` after its inbox transaction rolls back.

## Requirement: Azure-independent Notification tests

Automated Notification Worker tests MUST not require a live Azure Service Bus namespace.

#### Scenario: Run Notification tests without Service Bus configuration

- **WHEN** normal tests run without `ConnectionStrings:ServiceBus`
- **THEN** Notification contract-processing and settlement-order tests execute through local seams only.

## Requirement: Idempotent business-consumer settlement

The Payment, Inventory, and Notification Workers MUST retain Peek-Lock processing with automatic completion disabled and MUST explicitly complete both a newly processed message and a completed duplicate. A duplicate MUST be settled only after the durable processed-message lookup succeeds.

#### Scenario: Duplicate settlement

- **WHEN** a business subscription redelivers a message whose consumer-scoped key is already completed
- **THEN** the worker skips business processing
- **AND** explicitly completes the Service Bus message
- **AND** logs settlement only after the completion call succeeds.

## Requirement: Classified business-consumer failure settlement

Payment, Inventory, and Notification MUST retain Peek-Lock, disabled auto-completion, and explicit successful completion. They MUST distinguish permanent message failures from retryable processing failures.

#### Scenario: Transient processing failure

- **WHEN** business processing, inbox persistence, or an unknown processing operation fails without a permanent failure classification
- **THEN** the consumer MUST NOT complete or manually dead-letter the message
- **AND** the exception MUST propagate for Service Bus broker redelivery.

## Requirement: Immediate dead-lettering for permanent failures

Business consumers MUST manually dead-letter typed permanent failures and MUST NOT complete them. The dead-letter description MUST identify the consumer, native message ID, exception type, and concise failure detail without including the message body.

#### Scenario: Contract violation

- **WHEN** a message has an invalid subject, content type, body, JSON payload, or message ID
- **THEN** the consumer MUST dead-letter it with reason `MessageContractViolation`.

#### Scenario: Unsupported business status

- **WHEN** a valid `OrderCreated` message has a status unsupported by the receiving consumer
- **THEN** the consumer MUST dead-letter it with reason `UnsupportedBusinessStatus`
- **AND** the business operation MUST not be committed as completed.

## Requirement: Broker retry bound

The `payment`, `inventory`, and `notification` subscriptions MUST be configured with `MaxDeliveryCount = 5`.

#### Scenario: Repeated transient failure

- **WHEN** a message remains unsettled across the configured delivery attempts
- **THEN** Service Bus MUST provide terminal DLQ behavior with `MaxDeliveryCountExceeded`
- **AND** application code MUST NOT add a retry loop, Polly policy, or explicit abandon behavior.

## Requirement: Settlement and transport failure propagation

Completion, dead-letter settlement, lock-loss, and receiver transport failures MUST remain transport failures and MUST propagate without conversion to a permanent message failure.

#### Scenario: Completion fails after inbox commit

- **WHEN** explicit completion fails after successful business execution commits the inbox record
- **THEN** the exception MUST propagate
- **AND** a later delivery MUST be able to complete without repeating business execution.

## Requirement: Azure-independent idempotency tests

Automated idempotency and settlement tests MUST execute without a live Azure Service Bus namespace or Service Bus credentials. Persistence behavior MAY use the existing isolated PostgreSQL Testcontainers integration fixture.

#### Scenario: Run the automated suite without Azure

- **WHEN** unit and integration tests run without `ConnectionStrings:ServiceBus`
- **THEN** local message-processor seams and PostgreSQL persistence tests verify idempotency and settlement behavior.
