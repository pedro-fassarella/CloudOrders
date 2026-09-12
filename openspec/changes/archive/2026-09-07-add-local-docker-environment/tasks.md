# Implementation Tasks

## Planning gate

- [x] Create and review proposal, design, local-development specification, and implementation tasks.
- [x] Obtain approval for the Compose-only local PostgreSQL scope.

## Implementation

- [x] Add the API User Secrets identifier and make EF design-time configuration use the API host configuration pipeline.
- [x] Standardize Compose port interpolation and readiness timing while retaining PostgreSQL 17, the named volume, and restart behavior.
- [x] Add the safe `POSTGRES_PORT` example without changing `.env` Git-ignore behavior.
- [x] Document the Visual Studio, User Secrets, explicit migration, health, persistence, stop, and reset workflow.
- [x] Document volume initialization semantics and Compose/Testcontainers isolation.

## Verification

- [x] Validate rendered Compose configuration, PostgreSQL readiness, migrations through User Secrets, API health, and persisted Order retrieval after a normal Compose restart.
- [x] Run the full build, unit tests, and integration tests with Compose stopped for the integration-test check.
- [x] Confirm no real credentials, schema changes, automatic startup migration, API Dockerfile, `.dcproj`, or ADR were added.
