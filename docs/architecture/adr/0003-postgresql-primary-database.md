# ADR 0003 PostgreSQL Primary Database

## Status

Accepted

## Context

The long-term CloudOrders plan names PostgreSQL as the primary database. The walking skeleton must prove a real persistence boundary, but should avoid a broad schema or production database infrastructure.

## Decision

Use PostgreSQL with EF Core 10 for local development and the initial order table. Run the local database with Docker Compose and keep credentials in environment variables or User Secrets.

## Consequences

Integration tests validate behavior against the intended database engine. Local setup requires Docker and a configured password. Managed PostgreSQL, private networking, identity and production hardening remain future work.
