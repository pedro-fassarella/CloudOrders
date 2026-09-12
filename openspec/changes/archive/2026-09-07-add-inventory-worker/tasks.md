# Implementation Tasks

## Planning gate

- [x] Create and review the proposal, design, inventory and messaging specifications, and implementation tasks.
- [x] Confirm contract, business-status, and transport failures remain distinct.

## Implementation

- [x] Add `CloudOrders.Inventory.Worker` to the solution with safe Service Bus configuration and User Secrets support.
- [x] Reuse the existing Service Bus client/options registration and `OrderCreated` contract.
- [x] Implement deterministic `Pending` inventory reservation and explicit unsupported-status business rule.
- [x] Implement the long-lived `inventory` subscription processor with explicit successful completion.
- [x] Add Azure-independent message-processing and reservation tests.
- [x] Update README resource setup and simultaneous Payment/Inventory smoke-test guidance.

## Verification

- [x] Restore and build the solution.
- [x] Run unit tests without Service Bus configuration.
- [x] Run PostgreSQL Testcontainers integration tests without a live Service Bus namespace.
- [x] Perform the documented simultaneous Payment/Inventory Azure smoke test with developer-provisioned resources.
- [x] Confirm no Payment implementation changes, no idempotency, retry/DLQ, inventory persistence, Outbox, additional shared infrastructure, or automatic Inventory archive were added.
