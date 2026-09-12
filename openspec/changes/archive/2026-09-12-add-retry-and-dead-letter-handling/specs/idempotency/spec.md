# Idempotency Specification Changes

## Requirement: Failure-aware processed-message state

The existing `(ConsumerName, MessageId)` processed-message key MUST remain the source of truth for completed business execution.

#### Scenario: Failed execution remains retryable

- **WHEN** business processing or processed-message persistence fails before commit
- **THEN** no completed inbox state MUST be persisted
- **AND** a later delivery for the same consumer and message ID MAY execute the business operation again.

#### Scenario: Unsupported status is dead-lettered after rollback

- **WHEN** the business callback throws its typed unsupported-status failure
- **THEN** the processed-message transaction MUST roll back
- **AND** the message MUST be manually dead-lettered without a completed inbox state.

#### Scenario: Settlement failure after success

- **WHEN** the completed inbox record has committed and completion later fails
- **THEN** the next delivery MUST detect the completed key
- **AND** it MUST skip business execution and retry only settlement.
