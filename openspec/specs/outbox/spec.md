# Outbox Specification

## Requirement: Durable OrderCreated envelope

The outbox MUST store the existing serialized `OrderCreated` JSON body with a stable native MessageId, OrderId CorrelationId, subject `CloudOrders.Orders.OrderCreated`, content type `application/json`, and optional W3C trace metadata. Successfully published rows MUST remain stored.

#### Scenario: Retry a pending message

- **WHEN** a dispatcher retries a pending outbox row
- **THEN** it reuses the persisted MessageId, metadata, and payload.

## Requirement: Post-commit asynchronous dispatch

The API-hosted dispatcher MUST poll pending rows after the Order transaction commits and mark a row published only after Service Bus confirms the send.

#### Scenario: Send failure

- **WHEN** Service Bus send fails
- **THEN** the row remains pending for a later poll.

#### Scenario: Marking published fails after send

- **WHEN** Service Bus confirms a send but the published update fails
- **THEN** a later poll MAY publish the same MessageId again
- **AND** existing idempotent consumers safely skip duplicate business processing.

## Requirement: Single active dispatcher deployment

Initial deployment MUST run exactly one active Outbox Dispatcher. Concurrent dispatchers are unsupported in this increment.

#### Scenario: Scaled API

- **WHEN** multiple API replicas run
- **THEN** exactly one replica MUST use `Outbox:Enabled=true`
- **AND** the dispatcher MUST be disabled on every other replica.

Stable MessageIds and idempotent consumers do not replace dispatcher coordination. PostgreSQL row locking, `FOR UPDATE SKIP LOCKED`, leases, and equivalent claiming strategies remain deferred.
