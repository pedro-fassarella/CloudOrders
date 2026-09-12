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
- **AND** processing rolls back the inbox attempt and immediately dead-letters the message with reason `UnsupportedBusinessStatus`.

## Requirement: Notification permanent-failure handling

Notification MUST immediately dead-letter invalid `OrderCreated` contracts and unsupported notification statuses using the shared permanent-failure classification.

#### Scenario: Invalid contract

- **WHEN** Notification receives an invalid `OrderCreated` contract
- **THEN** it MUST dead-letter the message with reason `MessageContractViolation` without creating inbox state.

## Requirement: Idempotent Notification consumption

The Notification Worker MUST invoke the shared processed-message mechanism with `ConsumerIdentities.Notification` and the native Service Bus `MessageId` before invoking `INotificationProcessor`.

#### Scenario: Notification duplicate

- **WHEN** Notification receives a valid `OrderCreated` message whose Notification/`MessageId` key is already completed
- **THEN** `INotificationProcessor` is not invoked again
- **AND** the message is explicitly completed.
