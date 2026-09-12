# Design: OrderCreated Event Publishing

## Flow and boundaries

`POST /orders` continues to call `CreateOrderHandler`. The handler creates the Domain `Order`, awaits `IOrderStore.AddAsync`, then constructs an Application `OrderCreated` record from scalar values and awaits the existing `IMessagePublisher`. The API retains its `201 Created` response only after both operations succeed.

`OrderCreated` lives in `CloudOrders.Application.Messaging`; no Contracts project is introduced because there is still one integration boundary. Application does not reference Azure SDK types, and Domain remains independent of integration messaging.

## Contract and metadata

The JSON contract contains `orderId`, `customerId`, `status`, and `createdAtUtc`. It deliberately excludes EF entities, Domain objects, message IDs, and correlation IDs.

`status` is a string integration value. `CreateOrderHandler` explicitly maps `OrderStatus.Pending` to the contract constant `Pending`; it must not use `OrderStatus.ToString()`. Unknown future domain values fail fast until an intentional contract mapping is added.

The existing `ServiceBusMessageFactory` serializes with `System.Text.Json` web defaults and sets native metadata:

- `MessageId`: a newly generated GUID for this event/message.
- `CorrelationId`: the persisted order ID in `D` format.
- `Subject`: `CloudOrders.Orders.OrderCreated`.
- `ContentType`: `application/json`.

The configured topic remains `order-events`.

## Consistency and diagnostics

Persistence completes before publishing begins. A persistence failure prevents publication. If Service Bus publishing fails after `SaveChangesAsync` has committed, the exception propagates and the request receives standard server-error behavior; the order remains committed and may lack an event. This is an acknowledged temporary gap pending `add-outbox-pattern`. No rollback, retry, DLQ, idempotency, or recovery logic is introduced.

The Development-only probe endpoint, filtered `messaging-probe` subscription, and probe worker remain unchanged. Their filter excludes `OrderCreated`, leaving the probe useful for isolated transport diagnostics while no real-event consumer is introduced.

## Tests and configuration

Unit tests use recording stores and publishers to verify persistence-before-publication, explicit status mapping, metadata, persistence failure, and publish failure. Serialization tests create `ServiceBusMessage` instances without network access.

The integration `WebApplicationFactory` replaces `IMessagePublisher` with an in-memory singleton before requests resolve the handler. It can therefore exercise `POST /orders` and assert the captured `OrderCreated` event without Service Bus configuration or a live namespace. PostgreSQL integration testing remains Testcontainers-based.

No configuration keys, Azure resources, packages, worker behavior, migrations, or ADRs change. ADR 0004 remains the accepted topic/subscription decision.
