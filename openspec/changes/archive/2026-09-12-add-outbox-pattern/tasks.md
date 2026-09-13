# Implementation Tasks

## Planning and design

- [x] Inspect the order flow, persistence, Service Bus publishing, migrations, consumer idempotency, retry/DLQ handling, observability, README, ADRs, promoted specs, and archived changes.
- [x] Create proposal, design, Orders/Outbox/Messaging/Observability specifications, and implementation tasks.
- [x] Decide on PostgreSQL polling in a single API-hosted dispatcher and document the one-active-dispatcher deployment constraint.

## Implementation

- [x] Add focused outbox contracts, trace metadata, `OutboundMessage`, and publisher envelope support.
- [x] Persist Order and OutboxMessage in one explicit PostgreSQL transaction.
- [x] Add the outbox EF mapping, migration, pending index, retry metadata, and no-delete behavior.
- [x] Add the configurable polling dispatcher without leases, claims, or multi-instance coordination.
- [x] Instrument persistence and dispatch activities, logs, and low-cardinality metrics.
- [x] Preserve the typed Development probe publishing path and all business consumer behavior.

## Tests, documentation, and finalization

- [x] Add Azure-independent handler, envelope, dispatcher retry, and duplicate-send tests.
- [x] Add PostgreSQL integration coverage for atomic commit, rollback, and API non-publication.
- [x] Update README configuration, trace, failure, and single-dispatcher operational guidance.
- [x] Build, run all tests, and verify the EF model has no pending changes.
- [x] Promote approved specifications and archive after successful validation.
