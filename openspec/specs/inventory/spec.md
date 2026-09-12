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
- **AND** processing rolls back the inbox attempt and immediately dead-letters the message with reason `UnsupportedBusinessStatus`.

## Requirement: Inventory permanent-failure handling

Inventory MUST immediately dead-letter invalid `OrderCreated` contracts and unsupported inventory statuses using the shared permanent-failure classification.

#### Scenario: Invalid contract

- **WHEN** Inventory receives an invalid `OrderCreated` contract
- **THEN** it MUST dead-letter the message with reason `MessageContractViolation` without creating inbox state.

## Requirement: Idempotent Inventory consumption

The Inventory Worker MUST invoke the shared processed-message mechanism with `ConsumerIdentities.Inventory` and the native Service Bus `MessageId` before invoking `IInventoryProcessor`.

#### Scenario: Inventory duplicate

- **WHEN** Inventory receives a valid `OrderCreated` message whose Inventory/`MessageId` key is already completed
- **THEN** `IInventoryProcessor` is not invoked again
- **AND** the message is explicitly completed.
