# ADR 0001 Target .NET 10

## Status

Accepted

## Context

The retained CloudOrders plan references .NET 8, but the current project requirement is .NET 10. The repository is new and has no existing runtime compatibility constraint.

## Decision

Target .NET 10, ASP.NET Core 10, EF Core 10 and the C# version supported by the .NET 10 SDK.

## Consequences

The project can use the current platform baseline and its supported libraries. The legacy plan's .NET 8 prerequisites, Docker tags and examples require a later documentation update.
