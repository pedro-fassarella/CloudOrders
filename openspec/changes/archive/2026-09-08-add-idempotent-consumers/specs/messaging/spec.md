# Messaging Specification Changes

## Requirement: Idempotent business-consumer settlement

The Payment, Inventory, and Notification Workers MUST retain Peek-Lock processing with automatic completion disabled and MUST explicitly complete both a newly processed message and a completed duplicate. A duplicate MUST be settled only after the durable processed-message lookup succeeds.

#### Scenario: Duplicate settlement

- **WHEN** a business subscription redelivers a message whose consumer-scoped key is already completed
- **THEN** the worker skips business processing
- **AND** explicitly completes the Service Bus message
- **AND** logs settlement only after the completion call succeeds.

## Requirement: Failure settlement behavior

The idempotency change MUST NOT add custom retry, abandon, or dead-letter behavior.

#### Scenario: Processing or persistence failure

- **WHEN** contract processing, business processing, or processed-message persistence fails
- **THEN** the worker does not complete the message
- **AND** the existing Service Bus processor behavior remains responsible for subsequent delivery handling.

## Requirement: Azure-independent idempotency tests

Automated idempotency and settlement tests MUST execute without a live Azure Service Bus namespace or Service Bus credentials. Persistence behavior MAY use the existing isolated PostgreSQL Testcontainers integration fixture.

#### Scenario: Run the automated suite without Azure

- **WHEN** unit and integration tests run without `ConnectionStrings:ServiceBus`
- **THEN** local message-processor seams and PostgreSQL persistence tests verify idempotency and settlement behavior.
