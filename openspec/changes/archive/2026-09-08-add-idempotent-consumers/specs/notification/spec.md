# Notification Idempotency Specification

## Requirement: Idempotent Notification consumption

The Notification Worker MUST invoke the shared processed-message mechanism with the logical consumer identity `Notification` and the native Service Bus `MessageId` before invoking `INotificationProcessor`.

#### Scenario: Notification duplicate

- **WHEN** Notification receives a valid `OrderCreated` message whose `Notification`/`MessageId` key is already completed
- **THEN** `INotificationProcessor` is not invoked again
- **AND** the message is explicitly completed.

The existing deterministic simulated notification behavior and `Pending` status validation remain unchanged.
