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
- **AND** payment processing fails before message completion.
