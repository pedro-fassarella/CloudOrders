# Messaging Outbox Specification

## Requirement: Preserve OrderCreated transport contract

Outbox dispatch MUST preserve the existing `OrderCreated` JSON payload and native Service Bus MessageId, CorrelationId, Subject, and ContentType.

#### Scenario: Dispatch a stored OrderCreated event

- **WHEN** the dispatcher reconstructs an `OutboundMessage` from an outbox row
- **THEN** the Service Bus message uses the stored bytes and metadata unchanged
- **AND** Payment, Inventory, and Notification receive the existing contract.

The Development-only typed messaging probe remains unchanged.
