# Orders API Specification

## Requirement: Publish OrderCreated after persisted creation

For a valid `POST /orders`, the API MUST persist the new Pending order before publishing one `OrderCreated` integration event. It MUST return the existing `201 Created` response only after the publisher completes successfully.

#### Scenario: Persist and publish a new order

- **WHEN** a client submits a valid order creation request
- **THEN** the order is persisted with its existing HTTP response contract
- **AND** one `OrderCreated` event is published after persistence
- **AND** the event represents the persisted order values.

#### Scenario: Persistence fails

- **WHEN** persistence throws while creating an order
- **THEN** no `OrderCreated` publish is attempted.

#### Scenario: Publishing fails after persistence

- **WHEN** persistence succeeds and publishing then fails
- **THEN** the request uses standard server-error behavior
- **AND** the committed order is not rolled back
- **AND** this change provides no retry, idempotency, or recovery behavior.

## Requirement: Preserve order retrieval

`GET /orders/{id}` MUST preserve its existing successful, missing-order, and invalid-GUID behavior.

#### Scenario: Retrieve a persisted order

- **WHEN** an order created through `POST /orders` is retrieved by its ID
- **THEN** the response remains `200 OK` with the existing order response contract.
