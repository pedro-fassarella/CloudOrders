# Outbox Specification

## Requirement: Durable OrderCreated envelope

The outbox MUST persist the existing serialized `OrderCreated` payload with a stable native MessageId, OrderId CorrelationId, subject `CloudOrders.Orders.OrderCreated`, content type `application/json`, and optional W3C trace metadata.

#### Scenario: Retry a pending outbox message

- **WHEN** a dispatcher retries a pending outbox row
- **THEN** it publishes the persisted payload and exact stored metadata
- **AND** it reuses the original MessageId.

## Requirement: Post-commit asynchronous dispatch

The API-hosted dispatcher MUST poll pending records after the order transaction commits and mark a record published only after Service Bus confirms the send. Successfully published rows MUST NOT be deleted.

#### Scenario: Send succeeds

- **WHEN** Service Bus confirms an outbox send
- **THEN** the dispatcher records `published_at_utc` after confirmation.

#### Scenario: Send fails

- **WHEN** Service Bus send fails
- **THEN** the record remains pending for a later poll
- **AND** the dispatcher records only safe attempt metadata.

#### Scenario: Marking published fails after send

- **WHEN** Service Bus accepts the message but the database update fails
- **THEN** the record remains pending
- **AND** a later attempt may deliver the same MessageId again.

## Requirement: Single active dispatcher deployment

Initial deployment MUST run one active Outbox Dispatcher. Concurrent dispatcher instances are not supported.

#### Scenario: Scaled API deployment

- **WHEN** multiple API replicas run
- **THEN** exactly one replica MUST enable `Outbox:Enabled`
- **AND** every other replica MUST disable the dispatcher.

Consumer idempotency makes duplicate business execution safe but does not provide dispatcher coordination. Claims, leases, row locking, and `FOR UPDATE SKIP LOCKED` are deferred.
