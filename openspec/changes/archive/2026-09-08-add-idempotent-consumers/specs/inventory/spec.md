# Inventory Idempotency Specification

## Requirement: Idempotent Inventory consumption

The Inventory Worker MUST invoke the shared processed-message mechanism with the logical consumer identity `Inventory` and the native Service Bus `MessageId` before invoking `IInventoryProcessor`.

#### Scenario: Inventory duplicate

- **WHEN** Inventory receives a valid `OrderCreated` message whose `Inventory`/`MessageId` key is already completed
- **THEN** `IInventoryProcessor` is not invoked again
- **AND** the message is explicitly completed.

The existing deterministic simulated reservation and `Pending` status validation remain unchanged.
