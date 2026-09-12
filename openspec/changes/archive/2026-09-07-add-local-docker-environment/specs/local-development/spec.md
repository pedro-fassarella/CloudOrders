# Local Development Environment Specification

## Requirement: Local PostgreSQL startup

The repository MUST provide a PostgreSQL 17 service that a developer can start with `docker compose up -d postgres` after creating a local `.env` from `.env.example`.

#### Scenario: PostgreSQL reports readiness

- **WHEN** the local PostgreSQL service starts successfully
- **THEN** Docker Compose reports the service health using PostgreSQL readiness rather than only container process startup
- **AND** the database is reachable from the host at `localhost` and the configured `POSTGRES_PORT`.

## Requirement: Safe local configuration

Tracked configuration MUST NOT contain real database credentials. `.env.example` MUST provide only safe values and a password placeholder, and `.env` MUST remain ignored by Git.

#### Scenario: First local initialization

- **WHEN** a developer copies `.env.example` to `.env`
- **THEN** the documentation instructs the developer to replace the password placeholder before the first PostgreSQL volume initialization
- **AND** the API connection string is configured outside committed appsettings.

#### Scenario: Existing local volume

- **WHEN** a PostgreSQL volume has already been initialized
- **THEN** changing `POSTGRES_DB`, `POSTGRES_USER`, or `POSTGRES_PASSWORD` does not claim to reconfigure that database
- **AND** the documented intentional reset is `docker compose down -v` with a warning that it deletes local data.

## Requirement: Host API configuration and migrations

The API MUST continue using `ConnectionStrings:Postgres` and MUST support a Development User Secret with an environment-variable override.

#### Scenario: Environment variable precedence

- **WHEN** `ConnectionStrings:Postgres` is configured in User Secrets and `ConnectionStrings__Postgres` is configured in the environment
- **THEN** the environment variable supplies the effective connection string.

#### Scenario: Explicit schema application

- **WHEN** a developer applies local migrations
- **THEN** EF tooling uses the API configuration pipeline in Development
- **AND** schema application occurs through the explicit EF command rather than container initialization or API startup.

## Requirement: Persistent local data and isolated tests

The Compose database MUST retain data through normal container teardown, while integration tests remain independent from it.

#### Scenario: Normal Compose restart

- **WHEN** a developer runs `docker compose down` and later starts PostgreSQL again
- **THEN** data in the named volume remains available.

#### Scenario: Integration test execution

- **WHEN** integration tests run while the Compose service is stopped
- **THEN** they start and manage their own PostgreSQL 17 instance through Testcontainers
- **AND** they do not require Compose configuration or a local application connection string.
