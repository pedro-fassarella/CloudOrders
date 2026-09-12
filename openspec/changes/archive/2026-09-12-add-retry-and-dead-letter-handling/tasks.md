# Implementation Tasks

## Planning gate

- [x] Inspect current workers, Service Bus configuration, idempotency design, README, ADRs, promoted specs, and archived changes.
- [x] Complete the proposal, messaging/idempotency/consumer specs, design, and this task list before implementation.
- [x] Decide immediate DLQ for known permanent failures, broker redelivery for transient failures, and `MaxDeliveryCount = 5`.
- [x] Confirm canonical specs remain unchanged during apply and are promoted only during final archive.

## Implementation

- [x] Add shared Azure-independent permanent-failure and dead-letter detail types.
- [x] Classify contract and unsupported-business-status failures in each business consumer.
- [x] Add the dead-letter settlement delegate while preserving explicit completion and inbox ordering.
- [x] Update Payment, Inventory, and Notification Service Bus adapters to supply `DeadLetterMessageAsync`.
- [x] Preserve propagation for transient processing and settlement/transport failures; add no Polly, retry loop, or explicit abandon behavior.
- [x] Update README subscription setup to set `MaxDeliveryCount = 5` for business subscriptions and document DLQ behavior.

## Tests and verification

- [x] Add Azure-independent coverage for transient retryability, permanent DLQ, unsupported-status DLQ, completed duplicate safety, and failed inbox state.
- [x] Build the solution and run unit tests without Service Bus configuration.
- [x] Run PostgreSQL Testcontainers integration tests without a live Azure namespace.
- [x] Document the focused manual Azure smoke test.
- [x] Run the focused Azure smoke test: the permanent-contract message was immediately dead-lettered with `MessageContractViolation`; a controlled PostgreSQL outage caused redelivery until `MaxDeliveryCountExceeded`.
- [x] Review the implementation and validation results.

## Finalization, not part of apply

- [x] After successful implementation, verification, and review, promote approved change specs to canonical `openspec/specs`.
- [x] Archive the completed change and record validation results.
