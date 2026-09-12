# Add Retry and Dead-Letter Handling

## Objective

Define bounded Azure Service Bus failure handling for the Payment, Inventory, and Notification consumers while retaining Peek-Lock, explicit settlement, and durable `(ConsumerName, MessageId)` idempotency.

## Motivation

The business consumers currently leave every failure uncompleted. The Azure SDK then abandons an unsettled handler failure and Service Bus redelivers it, but the workers do not distinguish retryable failures from messages that cannot ever succeed. This causes malformed messages and unsupported business statuses to consume delivery attempts unnecessarily and gives their DLQ entries only the broker fallback reason.

## Scope

- Add Azure-independent typed permanent-failure and dead-letter settlement contracts shared by the three business consumers.
- Immediately dead-letter known permanent contract/message failures and unsupported business statuses with useful reason and description values.
- Continue broker redelivery for transient business, persistence, and unknown processing failures.
- Set `MaxDeliveryCount = 5` for the `payment`, `inventory`, and `notification` subscriptions in the documented Azure CLI setup.
- Preserve the existing PostgreSQL inbox and explicit settlement ordering.
- Add Azure-independent tests and a focused manual Azure smoke-test procedure.

## Out of Scope

- Polly, application retry loops, custom backoff, or explicit transient abandon calls.
- Outbox, provider integrations, DLQ replay tooling, observability expansion, deployment, IaC, CI/CD, or Messaging Probe changes.
- Canonical OpenSpec promotion or archival during this apply step.

## Success Criteria

1. Contract/message violations are dead-lettered immediately with reason `MessageContractViolation`.
2. Unsupported Payment, Inventory, and Notification statuses roll back inbox work and are dead-lettered immediately with reason `UnsupportedBusinessStatus`.
3. Transient processing and persistence failures are not completed or dead-lettered by application code and remain available for broker redelivery.
4. A completion failure after a completed inbox commit does not re-run business logic on redelivery.
5. Repeated unsettled transient failures are bounded by `MaxDeliveryCount = 5` on every business subscription.
6. Automated tests require neither Azure credentials nor a live Service Bus namespace.

## OpenSpec lifecycle

This proposal, its specs, design, and tasks are completed before implementation. Applying this change updates source, tests, README, and change-local OpenSpec artifacts only. Canonical-spec promotion and archival occur only after successful implementation, verification, and review.
