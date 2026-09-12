# Messaging Specification Changes

## Requirement: Classified business-consumer failure settlement

Payment, Inventory, and Notification MUST retain Peek-Lock, disabled auto-completion, and explicit successful completion. They MUST distinguish permanent message failures from retryable processing failures.

#### Scenario: Transient processing failure

- **WHEN** business processing, inbox persistence, or an unknown processing operation fails without a permanent failure classification
- **THEN** the consumer MUST NOT complete or manually dead-letter the message
- **AND** the exception MUST propagate for Service Bus broker redelivery.

## Requirement: Immediate dead-lettering for permanent failures

Business consumers MUST manually dead-letter typed permanent failures and MUST NOT complete them.

#### Scenario: Contract violation

- **WHEN** a message has an invalid subject, content type, body, JSON payload, or message ID
- **THEN** the consumer MUST dead-letter it with reason `MessageContractViolation`
- **AND** its description MUST identify the consumer, message ID, exception type, and concise validation detail.

#### Scenario: Unsupported business status

- **WHEN** a valid `OrderCreated` message has a status unsupported by the receiving consumer
- **THEN** the consumer MUST dead-letter it with reason `UnsupportedBusinessStatus`
- **AND** the business operation MUST not be committed as completed.

## Requirement: Broker retry bound

The `payment`, `inventory`, and `notification` subscriptions MUST be configured with `MaxDeliveryCount = 5`.

#### Scenario: Repeated transient failure

- **WHEN** a message remains unsettled across five broker delivery attempts
- **THEN** Service Bus MUST provide the terminal DLQ behavior
- **AND** application code MUST NOT add a retry loop or Polly policy.

## Requirement: Settlement and transport failure propagation

Completion, dead-letter settlement, lock-loss, and receiver transport failures MUST remain transport failures and MUST propagate without conversion to a permanent message failure.

#### Scenario: Completion fails after inbox commit

- **WHEN** explicit completion fails after successful business execution commits the inbox record
- **THEN** the exception MUST propagate
- **AND** a later delivery MUST be able to complete without repeating business execution.
