# Inventory Retry and Dead-Letter Specification

## Requirement: Inventory permanent-failure handling

Inventory MUST immediately dead-letter invalid `OrderCreated` contracts and unsupported inventory statuses using the shared permanent-failure classification.

#### Scenario: Unsupported Inventory status

- **WHEN** Inventory receives a valid `OrderCreated` with a non-`Pending` status
- **THEN** it MUST roll back the inbox attempt
- **AND** dead-letter the message with `UnsupportedBusinessStatus`.

Inventory's deterministic `Pending` simulation remains unchanged.
