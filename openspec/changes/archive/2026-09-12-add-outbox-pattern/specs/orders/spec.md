# Orders Outbox Specification

## Requirement: Atomic OrderCreated enqueue

For a valid `POST /orders`, the API MUST persist the Pending Order and one corresponding `OrderCreated` outbox record in the same PostgreSQL transaction.

#### Scenario: Order creation commits

- **WHEN** a client submits a valid order creation request
- **THEN** the Order and outbox record commit together
- **AND** the API returns the existing `201 Created` response without waiting for Service Bus.

#### Scenario: Outbox persistence fails

- **WHEN** inserting the outbox record fails during order creation
- **THEN** the Order insert rolls back
- **AND** no direct Service Bus publication is attempted.
