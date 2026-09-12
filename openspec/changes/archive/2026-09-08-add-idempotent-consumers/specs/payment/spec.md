# Payment Idempotency Specification

## Requirement: Idempotent Payment consumption

The Payment Worker MUST invoke the shared processed-message mechanism with the logical consumer identity `Payment` and the native Service Bus `MessageId` before invoking `IPaymentProcessor`.

#### Scenario: Payment duplicate

- **WHEN** Payment receives a valid `OrderCreated` message whose `Payment`/`MessageId` key is already completed
- **THEN** `IPaymentProcessor` is not invoked again
- **AND** the message is explicitly completed.

The existing deterministic simulated payment behavior and `Pending` status validation remain unchanged.
