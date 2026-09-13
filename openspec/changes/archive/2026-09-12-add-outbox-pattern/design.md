# Design: Transactional Outbox Pattern

## Atomic creation boundary

`CreateOrderHandler` creates the current `OrderCreated` contract and native metadata, starts the order/outbox activities, captures optional W3C context, and invokes the focused `IOrderStore.AddWithOutboxAsync` operation. It has no publisher dependency.

`EfOrderStore` uses the existing scoped `CloudOrdersDbContext` and an explicit PostgreSQL transaction. It inserts `orders` and `outbox_messages`, calls `SaveChangesAsync`, then commits. A failure rolls back both inserts. No generic Unit of Work or repository is added.

The outbox payload remains serialized `OrderCreated` JSON. The database record stores the stable MessageId, CorrelationId, subject, content type, traceparent, tracestate, timestamps, attempt count, and exception type separately. Published rows are retained.

## Dispatcher and transport boundary

`OutboxDispatcherHostedService` runs in the API process. Each poll resolves the focused outbox store, reads oldest pending rows up to `Outbox:BatchSize`, reconstructs `OutboundMessage`, sends it through `IMessagePublisher`, and then records `published_at_utc` only after the sender returns successfully. Failed sends leave rows pending and record only an exception type; no payload or exception text is stored in logs or failure metadata.

`OutboundMessage` is the provider-neutral transport envelope with MessageId, CorrelationId, Subject, ContentType, and serialized bytes. The existing typed publisher method remains for the Development messaging probe. Azure Service Bus maps this envelope directly to `ServiceBusMessage`.

## Failure, duplicate, and deployment semantics

Publisher failure leaves the row pending for the next poll. A process crash or database failure after successful send but before marking published leaves the row pending, so a later attempt sends the same MessageId again. Existing `(ConsumerName, MessageId)` inbox records prevent duplicate Payment, Inventory, and Notification business execution.

The dispatcher is deliberately single-instance only. Initial deployment must run one active dispatcher (`Outbox:Enabled=true`) and disable it on every other API replica. This increment does not use claims, leases, PostgreSQL row locks, or `FOR UPDATE SKIP LOCKED`. Idempotent consumers do not make concurrent polling a supported dispatcher topology.

Future multi-instance coordination may use PostgreSQL row locking with `FOR UPDATE SKIP LOCKED`, renewable leases, or equivalent claiming, but is deferred.

## Observability

The request records `cloudorders.outbox.persist`; each send records `cloudorders.outbox.dispatch` as a producer activity. W3C `traceparent` and `tracestate` are persisted as outbox metadata and parsed as the delayed dispatch activity parent. The Service Bus SDK can propagate its normal transport context without extending the business payload.

Logs include OrderId, MessageId, and CorrelationId. Metrics expose outbox persistence, an in-process pending gauge, and dispatch attempts tagged only by fixed outcome values. No payload, customer data, secret, connection string, exception message, or identifier metric dimension is emitted.
