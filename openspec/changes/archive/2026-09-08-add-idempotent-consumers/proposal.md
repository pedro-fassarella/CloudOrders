# Add Idempotent Consumers

## Objective

Make the Payment, Inventory, and Notification workers idempotent across Service Bus redelivery. Each worker must use the native Service Bus `MessageId` together with its logical consumer identity, persist successful processing in PostgreSQL, skip a previously completed message, and complete both first and duplicate deliveries safely.

## Motivation

The three workers currently validate and deserialize a message, invoke their simulated business processor, and then explicitly complete the Service Bus message. If completion fails or the lock expires after business processing, Service Bus can redeliver the message and the business processor will run again. Settlement alone does not prevent duplicate business execution.

The existing architecture already treats PostgreSQL as the primary persistence boundary and keeps each worker independent. A shared inbox table provides durable duplicate detection without creating three unrelated persistence models. The key must include the consumer because one `OrderCreated` message is intentionally delivered to Payment, Inventory, and Notification independently. Centralized stable consumer constants prevent accidental differences in casing or naming.

## Scope

- Add a shared PostgreSQL `processed_messages` inbox model with a unique composite key of logical consumer identity and native Service Bus `MessageId`.
- Add an Application persistence port and an Infrastructure implementation that coordinates the idempotent operation and durable processed state.
- Add the required EF Core mapping and migration without changing the existing Orders business schema.
- Register the shared mechanism and PostgreSQL connection for Payment, Inventory, and Notification workers.
- Wrap the existing business processors in the shared mechanism while keeping `SimulatedPaymentProcessor`, `SimulatedInventoryProcessor`, and `SimulatedNotificationProcessor` unchanged.
- Preserve explicit Peek-Lock settlement: a first delivery is completed after processing is durably recorded; a completed duplicate skips business processing and is completed.
- Add Azure-independent unit coverage and PostgreSQL Testcontainers coverage for persistence, duplicate handling, consumer isolation, completion behavior, and failure rollback.
- Document local worker PostgreSQL configuration and the distinction between business persistence and inbox persistence.

## Out of scope

- Retry policy, custom abandon behavior, or dead-letter policy.
- Outbox Pattern or a distributed transaction with Service Bus.
- Real payment, inventory, email, or SMS providers.
- New business events, business-result persistence, or changes to the simulated processors.
- Idempotency for the temporary `CloudOrders.Messaging.Worker` probe consumer.
- Observability expansion, Azure deployment, Managed Identity, CI/CD, or IaC.

## Success criteria

1. A valid first delivery invokes exactly one current business processor and is completed only after its processed state is committed.
2. A later delivery with the same native `MessageId` and same consumer identity does not invoke the business processor and is still completed.
3. The same native `MessageId` can be processed once independently by Payment, Inventory, and Notification.
4. A business or persistence failure does not leave a `Completed` inbox record and does not complete the Service Bus message.
5. A completed inbox record survives worker restart and is effective across worker instances using the same PostgreSQL database.
6. Automated tests do not require a live Azure Service Bus namespace; PostgreSQL tests use the existing isolated Testcontainers fixture.

## Atomicity decision

The shared coordinator uses PostgreSQL `INSERT ... ON CONFLICT (consumer_name, message_id) DO NOTHING` as its first ownership operation; it does not query before attempting that insert. The transaction that inserts the composite key invokes the current simulated business operation, marks the inbox row `Completed`, and commits as one short unit of work. A concurrent conflicting insert waits for the owning transaction, then observes its completed row and skips business processing. The Service Bus completion callback runs only after commit. If the business callback fails, the transaction rolls back and no completed record remains.

The current business processors are deterministic simulations with no external side effects or database writes, so this is the simplest reliable strategy for the present architecture. A database inbox cannot make an arbitrary future gateway/API side effect atomic with PostgreSQL: a process crash after that side effect and before the database commit could still cause another attempt. That stronger guarantee requires a transactional business state change or the deferred Outbox approach and is intentionally not introduced here.
