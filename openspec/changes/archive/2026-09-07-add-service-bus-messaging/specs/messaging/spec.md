# Messaging Specification

## Requirement: Development messaging probe publishing

In the Development environment, the API MUST expose `POST /messaging/probe`. It MUST publish one temporary messaging probe through the configured application publisher and return `202 Accepted` only after the publish operation succeeds.

The response MUST contain a generated `messageId`, `correlationId`, and subject `CloudOrders.Messaging.Probe`.

#### Scenario: Publish a probe

- **WHEN** a developer calls `POST /messaging/probe` with valid Service Bus configuration
- **THEN** a JSON probe message is sent to the configured topic
- **AND** the response is `202 Accepted` with the message and correlation identifiers
- **AND** the message has `MessageId`, `CorrelationId`, `Subject`, and `ContentType` metadata.

#### Scenario: Existing order creation remains synchronous

- **WHEN** a client calls `POST /orders`
- **THEN** the existing persistence and `201 Created` behavior is preserved
- **AND** no messaging probe or integration event is published.

## Requirement: Probe subscription consumption and settlement

The Messaging Worker MUST consume the configured probe subscription using Peek-Lock processing with automatic completion disabled.

#### Scenario: Complete a valid probe after processing

- **WHEN** the worker receives a JSON probe with subject `CloudOrders.Messaging.Probe` and content type `application/json`
- **THEN** it deserializes and logs the probe metadata
- **AND** it explicitly completes the received message only after successful processing.

#### Scenario: Do not complete a failed probe

- **WHEN** probe validation or JSON deserialization fails
- **THEN** the worker MUST NOT complete the message
- **AND** this change does not define a custom retry, dead-letter, or recovery policy.

## Requirement: Azure-independent automated tests

Normal unit and integration tests MUST NOT require a live Azure Service Bus namespace.

#### Scenario: Run the existing test suites without Service Bus configuration

- **WHEN** `dotnet test` runs without `ConnectionStrings:ServiceBus`
- **THEN** messaging serialization tests run without network access
- **AND** PostgreSQL integration tests continue to manage their own Testcontainers database.
