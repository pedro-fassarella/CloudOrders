# Payment Specification

## Requirement: Deterministic simulated payment

The Payment Worker MUST process the current `Pending` `OrderCreated` flow using a deterministic simulation without a real gateway, payment database, or schema.

#### Scenario: Process a Pending order

- **WHEN** a deserialized `OrderCreated` event has status `Pending`
- **THEN** the simulator succeeds
- **AND** it produces a stable reference derived from OrderId
- **AND** processing logs OrderId, MessageId, and CorrelationId.

## Requirement: Separate business-status validation

Order status handling MUST be separate from transport and JSON validation. The current simulator supports `Pending`; an unsupported status is a payment-entry business/contract failure, not a deserialization or transport failure.

#### Scenario: Unsupported status

- **WHEN** a valid `OrderCreated` JSON payload has an unsupported status
- **THEN** it is deserialized and passed to payment processing
- **AND** payment processing rolls back the inbox attempt and immediately dead-letters the message with reason `UnsupportedBusinessStatus`.

## Requirement: Payment permanent-failure handling

Payment MUST immediately dead-letter invalid `OrderCreated` contracts and unsupported payment statuses using the shared permanent-failure classification.

#### Scenario: Invalid contract

- **WHEN** Payment receives an invalid `OrderCreated` contract
- **THEN** it MUST dead-letter the message with reason `MessageContractViolation` without creating inbox state.

## Requirement: Idempotent Payment consumption

The Payment Worker MUST invoke the shared processed-message mechanism with `ConsumerIdentities.Payment` and the native Service Bus `MessageId` before invoking `IPaymentProcessor`.

#### Scenario: Payment duplicate

- **WHEN** Payment receives a valid `OrderCreated` message whose Payment/`MessageId` key is already completed
- **THEN** `IPaymentProcessor` is not invoked again
- **AND** the message is explicitly completed.
