# Orders API Specification

## Requirement: Transactional OrderCreated enqueue

For a valid `POST /orders`, the API MUST persist the new Pending Order and one corresponding `OrderCreated` outbox row in the same PostgreSQL transaction. It MUST return the existing `201 Created` response after that transaction commits and MUST NOT publish directly during request handling.

#### Scenario: Create an order

- **WHEN** a client submits a valid order creation request
- **THEN** the Order and its pending outbox row commit together
- **AND** the response retains the existing order contract.

#### Scenario: Outbox insertion fails

- **WHEN** persistence of the outbox record fails
- **THEN** the transaction rolls back the Order insert
- **AND** no Service Bus send is attempted.

## Requirement: Preserve order retrieval

`GET /orders/{id}` MUST preserve its existing successful, missing-order, and invalid-GUID behavior.
