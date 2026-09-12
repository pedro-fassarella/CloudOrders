# Notification Retry and Dead-Letter Specification

## Requirement: Notification permanent-failure handling

Notification MUST immediately dead-letter invalid `OrderCreated` contracts and unsupported notification statuses using the shared permanent-failure classification.

#### Scenario: Unsupported Notification status

- **WHEN** Notification receives a valid `OrderCreated` with a non-`Pending` status
- **THEN** it MUST roll back the inbox attempt
- **AND** dead-letter the message with `UnsupportedBusinessStatus`.

Notification's deterministic `Pending` simulation remains unchanged.
