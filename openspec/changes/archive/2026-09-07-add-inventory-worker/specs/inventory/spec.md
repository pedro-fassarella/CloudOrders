# Inventory Specification

## Requirement: Deterministic simulated reservation

The Inventory Worker MUST process the current `Pending` `OrderCreated` flow with a deterministic simulated reservation and without a real inventory system, persistence, database, or schema.

#### Scenario: Process a Pending order

- **WHEN** a deserialized `OrderCreated` event has status `Pending`
- **THEN** the simulator succeeds
- **AND** it produces `simulated-reservation-{OrderId:N}`
- **AND** processing logs OrderId, MessageId, CorrelationId, and the reservation reference.

## Requirement: Separate business-status validation

Order status handling MUST be separate from transport and JSON validation. The simulator supports `Pending`; an unsupported status is an inventory business-status failure rather than a deserialization or transport failure.

#### Scenario: Unsupported status

- **WHEN** valid `OrderCreated` JSON has an unsupported status
- **THEN** it is deserialized and passed to inventory processing
- **AND** processing fails before message completion.
