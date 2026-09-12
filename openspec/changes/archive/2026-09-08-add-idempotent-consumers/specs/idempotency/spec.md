# Idempotency Specification

## Requirement: Shared durable processed-message state

Payment, Inventory, and Notification MUST use a durable shared processed-message model backed by PostgreSQL. In-memory duplicate tracking MUST NOT be used as the source of truth.

#### Scenario: State survives process restart

- **WHEN** a worker has committed a message as completed and the worker process is restarted
- **THEN** a later delivery can read the completed state from PostgreSQL
- **AND** the business operation is not invoked again.

## Requirement: Consumer-scoped duplicate identity

The duplicate key MUST be the pair of the logical consumer identity and the native Service Bus `MessageId`. Payment, Inventory, and Notification MUST use the distinct centralized `ConsumerIdentities` constants.

#### Scenario: One event fans out to all business consumers

- **WHEN** Payment, Inventory, and Notification each receive an `OrderCreated` copy with the same native `MessageId`
- **THEN** each consumer can execute its own business operation once
- **AND** a duplicate for one consumer does not suppress processing by either of the other consumers.

## Requirement: Durable first-delivery processing

For a valid message whose consumer-scoped key is not completed, the worker MUST invoke its business processor, durably record the key as `Completed`, and only then request Service Bus completion.

#### Scenario: First delivery succeeds

- **WHEN** a valid `OrderCreated` message is delivered for the first time to a business consumer
- **THEN** its business processor is invoked
- **AND** its consumer-scoped key is committed as completed
- **AND** the Service Bus message is completed after the database commit.

## Requirement: Safe duplicate delivery

When a consumer-scoped key is already completed, the worker MUST skip the business processor and MUST still complete the redelivered Service Bus message.

#### Scenario: Completed message is redelivered

- **WHEN** the same consumer receives the same native `MessageId` again after a successful first delivery
- **THEN** the previously completed state is detected
- **AND** the business processor is not invoked again
- **AND** the duplicate delivery is completed successfully.

## Requirement: Failure is not completed

If contract validation, deserialization, business processing, or processed-state persistence fails, the worker MUST NOT complete the Service Bus message. A failed business operation MUST NOT be committed as `Completed`.

#### Scenario: Business processing fails

- **WHEN** the business processor throws for a newly claimed message
- **THEN** the processed-state transaction is rolled back or otherwise remains not completed
- **AND** the Service Bus message is not completed
- **AND** a later delivery is not treated as a successfully completed duplicate solely because of the failed attempt.

## Requirement: Atomic processing boundary

The processed-state implementation MUST coordinate the claim, current simulated business callback, and `Completed` recording in one database transaction. The claim MUST begin with an atomic PostgreSQL `INSERT ... ON CONFLICT (consumer_name, message_id) DO NOTHING`; it MUST NOT query for the key before attempting the insert. The Service Bus completion callback MUST execute after that transaction commits.

#### Scenario: Concurrent delivery for one consumer

- **WHEN** two deliveries for the same consumer and native `MessageId` attempt processing concurrently
- **THEN** PostgreSQL's composite primary key permits only one transaction to acquire the key
- **AND** only the successful claimant invokes the current business callback
- **AND** the other delivery observes `Completed`, skips business processing, and completes safely.

#### Scenario: Completion fails after database commit

- **WHEN** the database has committed the completed key but the Service Bus completion call fails
- **THEN** a later delivery skips the business processor using the durable completed state
- **AND** the later delivery can safely retry Service Bus completion.

The current simulated processors have no external side effects. Exactly-once coordination with future external providers remains deferred until a transactional business-state or Outbox design exists.
