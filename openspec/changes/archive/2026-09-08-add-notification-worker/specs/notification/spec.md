# Notification Specification

## Requirement: Deterministic simulated notification

The Notification Worker MUST process the current `Pending` `OrderCreated` flow with a deterministic simulated notification and without a real email or SMS provider, external HTTP call, persistence, database, or delay.

#### Scenario: Process a Pending order

- **WHEN** a deserialized `OrderCreated` event has status `Pending`
- **THEN** the simulator succeeds with channel `simulated`
- **AND** it produces `simulated-notification-{OrderId:N}`
- **AND** processing logs OrderId, MessageId, CorrelationId, notification channel, and notification reference.

## Requirement: Separate business-status validation

Order status handling MUST be separate from transport and JSON validation. The simulator supports `Pending`; an unsupported status is a notification business-status failure rather than a deserialization or transport failure.

#### Scenario: Unsupported status

- **WHEN** valid `OrderCreated` JSON has an unsupported status
- **THEN** it is deserialized and passed to notification processing
- **AND** processing fails before message completion.
