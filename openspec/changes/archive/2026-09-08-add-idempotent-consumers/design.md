# Design: Idempotent Consumers

## Current boundaries

`ServiceBusPaymentWorker`, `ServiceBusInventoryWorker`, and `ServiceBusNotificationWorker` remain the Azure Service Bus boundaries. Their message callbacks continue to pass primitive message metadata and a completion delegate into their Azure-independent message processors. The three message processors continue to own contract validation, JSON deserialization, business processor invocation, success logging, and settlement ordering.

The business processors remain unchanged. They still support only the current `Pending` flow and return deterministic simulated results without business persistence, provider calls, delays, or new events. Inbox persistence is infrastructure state and does not turn Payment, Inventory, or Notification business processing into a persisted workflow.

## Evaluated persistence models

- An in-memory set is rejected because it is lost on restart and is not shared by worker instances.
- Three independent tables are rejected because they duplicate the mechanism and make cross-consumer behavior harder to verify.
- A single `MessageId` key is rejected because the same fan-out event must be processed once by each consumer.
- A row inserted only after business processing is insufficient as the complete coordination mechanism: concurrent deliveries can both pass the initial read, and a crash can occur between the business call and the insert.
- A shared PostgreSQL inbox with a composite key and a transaction-owned claim is selected. PostgreSQL provides durable state and the unique constraint arbitrates concurrent attempts across worker instances.

## Inbox model

Add a `ProcessedMessage` entity to the existing `CloudOrdersDbContext` and map it to `processed_messages`:

| Column | Purpose |
| --- | --- |
| `ConsumerName` | Stable logical identity such as `Payment`, `Inventory`, or `Notification`. |
| `MessageId` | Native Azure Service Bus `MessageId`, stored unchanged. |
| `State` | Internal `Processing` state while the transaction is open, then durable `Completed` state. |
| `StartedAtUtc` | UTC timestamp for the current transaction attempt. |
| `CompletedAtUtc` | UTC timestamp populated only when processing succeeds. |

The primary key is `(ConsumerName, MessageId)`. The model uses bounded required strings and a database check/convention for the two supported states. Only `Completed` rows are durable after a successful transaction; a failed operation rolls back the inserted `Processing` row rather than recording it as completed. Add an EF Core migration for this table and update the model snapshot. The existing `orders` table and migration behavior remain unchanged.

The logical consumer names are centralized as `ConsumerIdentities.Payment`, `ConsumerIdentities.Inventory`, and `ConsumerIdentities.Notification`; workers do not define local string variants. They are not inferred from mutable message payloads. The native `MessageId` is passed through unchanged and is rejected when empty. No hash, order ID, correlation ID, or generated replacement is used as the duplicate key.

## Shared application port and Infrastructure coordinator

Add an Application port with a result that distinguishes a newly executed operation from an already completed duplicate. Its operation shape should be generic so the existing business receipt can be returned without changing the business processor interfaces:

```text
ExecuteOnceAsync<T>(consumerName, messageId, operation, cancellationToken)
    -> { AlreadyProcessed, Result }
```

Infrastructure implements the port with `IDbContextFactory<CloudOrdersDbContext>` so the singleton message processors do not hold a scoped `DbContext`. The coordinator:

1. Starts a PostgreSQL transaction.
2. Uses `INSERT ... ON CONFLICT (consumer_name, message_id) DO NOTHING` as the first ownership operation. It does not check for an existing key before inserting.
3. If the insert returns the newly inserted `Processing` row, retains the unique-key claim and invokes the supplied business callback while the transaction is open. Only this path can invoke the callback.
4. Changes the row to `Completed`, sets `CompletedAtUtc`, saves, and commits only after the callback succeeds.
5. If the insert conflicts, reads the resolved row only after the conflict: `Completed` returns `AlreadyProcessed`; a persisted `Processing` row is a persistence failure and is not completed.
6. Rolls back on callback, cancellation, or database failure and lets the exception propagate. A conflicting insert waits for the owner to commit or roll back; after rollback, it may acquire the key as a new attempt.

The transaction is intentionally short for the current no-I/O simulations. Future provider calls must not be added inside this transaction as a substitute for a distributed transaction.

## Worker reuse and settlement flow

Each message processor follows the same sequence:

1. Validate subject and content type, reject an empty body where applicable, and deserialize `OrderCreated`.
2. Call the shared coordinator with its centralized consumer identity and the message's native `MessageId`.
3. On a new key, invoke the unchanged business processor and log its existing deterministic result after the coordinator commits.
4. On a completed duplicate, do not invoke the business processor and log a concise duplicate-skip event with consumer and `MessageId`.
5. Invoke the existing completion delegate for both new and duplicate deliveries.
6. Log successful settlement only after the completion delegate returns.

Contract failures still happen before the coordinator and are not completed. The three workers retain `AutoCompleteMessages = false`, Peek-Lock, and `MaxConcurrentCalls = 1`. The shared database key also protects the same consumer when more than one worker instance is running.

If Service Bus completion fails after the database commit, the message can be redelivered; its durable `Completed` row makes the next attempt skip business processing and retry only settlement. If business processing fails, no completed row is committed and the message is left unsettled for the existing Service Bus behavior. This change does not configure custom retry, abandon, or DLQ handling.

## Registration and configuration

Add a focused Infrastructure registration such as `AddProcessedMessagePersistence` rather than calling the API's full Orders registration from workers. It registers the PostgreSQL `DbContextFactory` and the shared port implementation. The three worker `Program.cs` files call this registration in addition to the existing Service Bus registration.

Add an empty committed `ConnectionStrings:Postgres` setting to each business worker's appsettings and document User Secrets/environment-variable configuration. The API remains the design-time startup project for generating and applying the shared EF migration. Workers assume the existing explicit migration workflow has initialized the database; they do not run migrations on startup.

## Tests

Keep the existing message-processor seam and completion delegate so no Azure SDK or namespace is required in unit tests. Update each worker test fixture to inject a local fake `IProcessedMessageStore` and assert:

- first delivery invokes the business fake once and completes once;
- a second delivery with the same consumer and `MessageId` skips the business fake and still invokes completion;
- a business exception produces no completion and no completed state;
- Payment, Inventory, and Notification use distinct consumer identities for the same `MessageId`.

Add persistence-focused integration tests using the existing PostgreSQL Testcontainers fixture and the real Infrastructure store. Verify durable state across fresh store/context instances, composite-key isolation, rollback after a failed callback, and the concurrent/same-key claim behavior where practical. These tests use no Service Bus client or live Azure configuration. The manual Azure smoke flow remains a separate developer check and is not part of automated tests.

## Deferred boundary

This design establishes durable consumer-side deduplication for the current simulations. It does not claim exactly-once execution for an arbitrary external side effect. The future Outbox change, or a future business persistence transaction that includes the effect and inbox state, must address that boundary before real payment, inventory, or notification providers are introduced.
