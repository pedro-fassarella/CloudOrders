# Observability Specification

## Requirement: Correlated order and outbox telemetry

The API MUST create application telemetry for an accepted order creation that identifies the order, native message, and business correlation without placing those identifiers in metric dimensions.

#### Scenario: Create an order

- **WHEN** `POST /orders` commits an order and its `OrderCreated` outbox row
- **THEN** the application trace includes `OrderId`, `MessageId`, and `CorrelationId`
- **AND** it records outbox persistence telemetry without customer or identifier metric attributes.

#### Scenario: Dispatch a pending outbox row

- **WHEN** the API-hosted dispatcher sends an outbox message
- **THEN** it emits correlated dispatch tracing, structured logs, and low-cardinality pending/attempt metrics
- **AND** it marks the row published only after Service Bus confirms the send.

## Requirement: Delayed W3C trace continuity

The API MUST persist optional W3C trace context as outbox metadata and MUST NOT add it to the `OrderCreated` business payload.

#### Scenario: Dispatch with stored trace context

- **WHEN** a pending outbox row contains valid traceparent and tracestate metadata
- **THEN** the dispatcher starts its activity with that context as parent
- **AND** the event JSON contract remains unchanged.

## Requirement: Business-worker observability

Payment, Inventory, and Notification MUST emit correlated application processing spans, structured lifecycle logs, and low-cardinality outcome metrics while preserving existing settlement behavior.

#### Scenario: Process a valid message

- **WHEN** a worker processes and completes a valid `OrderCreated` delivery
- **THEN** it records consumer and processed outcome telemetry
- **AND** its logs include available order and message correlation identifiers
- **AND** it preserves the existing inbox-before-completion ordering.

#### Scenario: Handle a duplicate or dead-lettered delivery

- **WHEN** a worker identifies a completed duplicate or immediately dead-letters a typed permanent failure
- **THEN** it records the corresponding outcome and settlement telemetry
- **AND** it does not modify the existing duplicate, retry, or dead-letter semantics.

## Requirement: Sensitive-data exclusion

Logs, activity tags, and metric dimensions MUST NOT include message bodies, customer payloads, credentials, connection strings, SQL parameter values, or sensitive configuration.

#### Scenario: Export telemetry for a message

- **WHEN** a message is processed or rejected
- **THEN** telemetry identifies the message through its native ID and fixed outcome/category fields only
- **AND** it does not serialize the message body or secret configuration.

## Requirement: Exporter and Service Bus configuration

Development MUST default to console trace and metric export, OTLP MUST be opt-in, and non-Development hosts MUST default to no exporter. Service Bus SDK activities MUST use only the feature opt-in required by the resolved SDK.

#### Scenario: Run tests without external telemetry

- **WHEN** automated unit or integration tests execute in the Testing environment
- **THEN** no OTLP endpoint, Azure namespace, or external telemetry backend is required.

#### Scenario: Enable Service Bus transport spans locally

- **WHEN** a developer sets `AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE=true` before process startup
- **THEN** the registered exact Service Bus SDK ActivitySource can emit send/process/settlement telemetry
- **AND** no application `AppContext` switch is applied.
