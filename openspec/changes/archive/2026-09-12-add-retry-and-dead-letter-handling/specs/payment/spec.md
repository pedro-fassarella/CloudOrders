# Payment Retry and Dead-Letter Specification

## Requirement: Payment permanent-failure handling

Payment MUST immediately dead-letter invalid `OrderCreated` contracts and unsupported payment statuses using the shared permanent-failure classification.

#### Scenario: Unsupported Payment status

- **WHEN** Payment receives a valid `OrderCreated` with a non-`Pending` status
- **THEN** it MUST roll back the inbox attempt
- **AND** dead-letter the message with `UnsupportedBusinessStatus`.

Payment's deterministic `Pending` simulation remains unchanged.
