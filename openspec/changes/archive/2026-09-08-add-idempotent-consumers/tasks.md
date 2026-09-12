# Implementation Tasks

## Planning gate

- [x] Inspect the promoted OpenSpec specs, ADRs, current persistence architecture, worker implementations, worker tests, and archived worker changes.
- [x] Evaluate in-memory, per-consumer, post-processing, and shared transactional inbox options.
- [x] Prepare the proposal, idempotency/messaging/consumer specifications, design, and implementation tasks.
- [x] Obtain explicit approval for this implementation scope.

## Persistence and shared mechanism

- [x] Add centralized consumer identity constants plus the Application processed-message/idempotent-operation port and result type without Azure or EF Core dependencies.
- [x] Add the `ProcessedMessage` EF model, `processed_messages` mapping, composite primary key, state/timestamp fields, and model snapshot update.
- [x] Add an Infrastructure implementation using `IDbContextFactory<CloudOrdersDbContext>` and a PostgreSQL transaction whose first claim operation is `INSERT ... ON CONFLICT DO NOTHING`, then owns the business callback and completed recording.
- [x] Add a focused worker registration for the shared processed-message persistence and validate the required PostgreSQL connection string.
- [x] Generate the explicit EF migration for the inbox table; do not run migrations automatically at worker startup.

## Worker integration

- [x] Add `ConnectionStrings:Postgres` safe defaults and local User Secrets/environment-variable guidance to Payment, Inventory, and Notification workers.
- [x] Update each message processor to pass its fixed logical consumer identity and native `MessageId` to the shared mechanism.
- [x] Preserve contract validation before idempotency, unchanged business processor interfaces/implementations, explicit Peek-Lock configuration, and completion after durable processing.
- [x] Complete completed duplicates safely and add duplicate-skip logging without expanding general observability.
- [x] Ensure failed business or persistence operations propagate without completion and do not leave a completed inbox record.

## Tests and documentation

- [x] Update Payment, Inventory, and Notification unit tests with local processed-message fakes to prove first delivery, duplicate skip, duplicate completion, and failed-processing behavior.
- [x] Add cross-consumer coverage proving one MessageId is independently processed once by Payment, Inventory, and Notification.
- [x] Add PostgreSQL Testcontainers coverage for durable state across contexts/restarts, composite-key isolation, transaction rollback, and same-key claim behavior; keep all tests Azure-free.
- [x] Update README worker configuration and test guidance to describe the shared inbox and the manual Azure smoke test boundary.
- [x] Confirm the probe worker, business processors, Orders schema behavior, and all out-of-scope retry/DLQ, Outbox, provider, observability, deployment, CI/CD, and IaC concerns remain unchanged.

## Verification

- [x] Restore and build the solution.
- [x] Apply the new migration in an isolated PostgreSQL test database and verify no pending model changes remain.
- [x] Run unit tests without Service Bus configuration.
- [x] Run PostgreSQL Testcontainers integration tests without a live Azure namespace.
- [x] Review the diff for the planning scope and record validation results; do not archive this change until implementation is explicitly approved and completed.
