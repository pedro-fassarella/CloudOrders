# Implementation Tasks

## Planning gate

- [x] Create and review the proposal, design, messaging specification, and implementation tasks.
- [x] Obtain approval for the temporary Development-only probe scope.

## Implementation

- [x] Add centrally managed Service Bus and Worker hosting packages; add the Messaging Worker to the solution.
- [x] Add the Application publisher port, metadata, and temporary probe contract.
- [x] Implement singleton Service Bus client/sender registration and the Infrastructure publisher with JSON and native metadata.
- [x] Add the Development-only probe endpoint without changing `POST /orders`.
- [x] Implement the long-lived Worker processor with explicit successful completion.
- [x] Add safe committed configuration, User Secrets guidance, Azure resource creation instructions, and the smoke test documentation.
- [x] Add ADR 0004 for the Topic/Subscription architecture decision.
- [x] Add Azure-independent unit coverage for metadata and JSON serialization.

## Verification

- [x] Restore and build the solution.
- [x] Run unit and PostgreSQL Testcontainers integration tests without Service Bus configuration.
- [x] Perform the documented live Azure Service Bus smoke test with developer-provisioned resources.
- [x] Confirm no real `OrderCreated`, retry/DLQ policy, Outbox, Azure deployment, IaC, or credentials were added.
