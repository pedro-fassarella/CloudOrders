# Outbox Observability Specification

## Requirement: Correlated outbox telemetry

The API MUST trace outbox persistence and delayed dispatch using OrderId, MessageId, and CorrelationId without using those identifiers as metric dimensions.

#### Scenario: Dispatch a pending row

- **WHEN** the dispatcher sends an outbox message
- **THEN** it emits an outbox dispatch span, structured lifecycle logs, and low-cardinality pending/attempt metrics
- **AND** it does not log payloads, secrets, customer data, or exception messages.

## Requirement: Delayed W3C continuity

W3C trace context MUST be stored as outbox metadata, not inside `OrderCreated` JSON.

#### Scenario: Valid persisted trace context

- **WHEN** a dispatcher reads a valid stored traceparent and tracestate
- **THEN** it uses that context as the dispatch activity parent
- **AND** the business event payload remains unchanged.
