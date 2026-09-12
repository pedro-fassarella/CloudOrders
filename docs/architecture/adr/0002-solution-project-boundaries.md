# ADR 0002 Solution Project Boundaries

## Status

Accepted

## Context

CloudOrders is intended to evolve toward asynchronous processing, but the first increment must remain small and testable. A single project would obscure the persistence boundary, while the complete target architecture would introduce premature workers and messaging.

## Decision

Use four production projects: Domain, Application, Infrastructure and Api. Use separate UnitTests and IntegrationTests projects. Keep Domain independent, define persistence ports in Application, implement them in Infrastructure, and compose dependencies in Api.

Do not create a separate Contracts project until more than one integration boundary requires shared contracts.

## Consequences

The initial dependency graph is explicit and easy to test. Future workers can reuse Application and Domain without forcing messaging abstractions into the walking skeleton.
