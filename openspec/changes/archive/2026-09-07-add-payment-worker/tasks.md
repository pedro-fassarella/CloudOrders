# Implementation Tasks

## Planning gate

- [x] Create and review the proposal, design, payment and messaging specifications, and implementation tasks.
- [x] Confirm that message-contract failures are distinct from payment business-status validation.

## Implementation

- [x] Add `CloudOrders.Payment.Worker` to the solution with safe Service Bus configuration and User Secrets support.
- [x] Reuse the existing Service Bus client/options registration and `OrderCreated` contract.
- [x] Implement a deterministic `Pending` payment simulation and explicit unsupported-status business rule.
- [x] Implement the long-lived `payment` subscription processor with explicit successful completion.
- [x] Add Azure-independent message-processing and payment tests.
- [x] Update README resource setup and payment-worker smoke-test guidance.

## Verification

- [x] Restore and build the solution.
- [x] Run unit tests without Service Bus configuration.
- [x] Run PostgreSQL Testcontainers integration tests without a live Service Bus namespace.
- [x] Confirm no idempotency, retry/DLQ, additional payment infrastructure, or automatic archive was added.
