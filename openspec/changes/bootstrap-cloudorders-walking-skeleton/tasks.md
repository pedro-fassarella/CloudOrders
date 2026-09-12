# Implementation Tasks

## Planning gate

- [x] Create proposal, design, orders specification and health specification.
- [x] Review the planning artifacts and explicitly approve implementation.

## Solution and configuration

- [x] Create the .NET 10 solution and six projects.
- [x] Configure nullable reference types, implicit usings, SDK pinning and central package versions.
- [x] Add project references with the documented dependency direction.

## Domain and application

- [x] Implement `Order` and `OrderStatus` with creation validation.
- [x] Implement `IOrderStore`, `CreateOrderCommand` and `CreateOrderHandler`.
- [x] Add unit tests for valid creation, invalid customer IDs and persistence invocation.

## Infrastructure

- [x] Implement `CloudOrdersDbContext` and the PostgreSQL order mapping.
- [x] Implement `EfOrderStore` and the design-time context factory.
- [x] Generate and review the initial EF Core migration.
- [x] Add `compose.yaml`, `.env.example` and secret-safe local configuration.

## API

- [x] Implement `GET /health` and `POST /orders`.
- [x] Register application and infrastructure services.
- [x] Expose the development OpenAPI document.
- [x] Add integration tests for health, invalid input and persisted creation.

## Documentation and decisions

- [x] Add the minimum README for local setup and test execution.
- [x] Add ADRs for .NET 10, project boundaries and PostgreSQL.
- [x] Record the .NET 8 conflicts from the legacy plan without rewriting that document.

## Verification

- [x] Run restore, build and unit tests.
- [x] Start PostgreSQL with Docker Compose and run the integration suite.
- [x] Run a local API smoke test for `/health` and `/orders`.
- [x] Confirm no secrets or Azure resources were added.
