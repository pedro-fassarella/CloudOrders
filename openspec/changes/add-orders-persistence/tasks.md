# Implementation Tasks

## Planning gate

- [x] Create and review proposal, design, Orders specification and Health specification.
- [x] Obtain explicit approval for this implementation scope.

## Implementation

- [x] Align central package versions and add Testcontainers.PostgreSql to integration tests.
- [x] Extend the order store and application with retrieval by ID; add unit coverage.
- [x] Expose `GET /orders/{id}` using standard Guid binding and document its response contract.
- [x] Register the EF Core PostgreSQL health check without changing the schema.
- [x] Replace manually configured integration persistence tests with a PostgreSQL Testcontainers fixture and cover the HTTP flow.
- [x] Update README test instructions without changing compose.yaml or .env.example.

## Verification

- [x] Confirm the EF model has no pending schema changes against the existing migration.
- [x] Run restore, full solution build, unit tests, integration tests and NuGet vulnerability audit.
- [x] Record validation results and remaining warnings in this change; do not archive it.
