# Implementation Tasks

## Planning gate

- [x] Create and review the proposal, design, notification and messaging specifications, and implementation tasks.
- [x] Confirm contract, business-status, and transport failures remain distinct.

## Implementation

- [x] Add `CloudOrders.Notification.Worker` to the solution with safe Service Bus configuration and User Secrets support.
- [x] Reuse the existing Service Bus client/options registration and `OrderCreated` contract.
- [x] Implement deterministic `Pending` notification simulation and explicit unsupported-status business rule.
- [x] Implement the long-lived `notification` subscription processor with explicit successful completion.
- [x] Add Azure-independent message-processing and notification tests.
- [x] Update README resource setup and simultaneous Payment/Inventory/Notification smoke-test guidance.

## Verification

- [x] Restore and build the solution.
- [x] Run unit tests without Service Bus configuration.
- [x] Run PostgreSQL Testcontainers integration tests without a live Service Bus namespace.
- [x] Perform the documented simultaneous Payment/Inventory/Notification Azure smoke test with developer-provisioned resources for OrderId `29ed3470-300c-4cbd-9158-f1bd354277ee`; all business subscriptions finished with zero active and dead-letter messages, while `messaging-probe` remained unused.
- [x] Confirm no Payment or Inventory implementation changes, idempotency, retry/DLQ, notification persistence, Outbox, additional shared infrastructure, or automatic archive were added.
