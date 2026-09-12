# Implementation Tasks

## Planning and design

- [x] Inspect the API, publisher, workers, messaging flow, retry/idempotency behavior, README, ADRs, promoted specifications, and archived changes.
- [x] Create proposal, design, tasks, and the observability specification.
- [x] Select Development console plus opt-in OTLP exporter behavior.
- [x] Verify the exact resolved Azure Service Bus package documentation before adding any opt-in; do not add an `AppContext` switch.

## Implementation

- [x] Add centrally managed stable OpenTelemetry and Npgsql observability package references.
- [x] Add provider-neutral CloudOrders activities, metrics, tag conventions, and logging scopes.
- [x] Register OpenTelemetry resources, sources, meters, Npgsql/EF Core/runtime instrumentation, and configurable exporters.
- [x] Instrument API order creation and the Payment, Inventory, and Notification processing paths.
- [x] Preserve existing message contracts, inbox ordering, retry, completion, and dead-letter behavior.
- [x] Configure JSON console logs and Development exporter defaults.
- [x] Update README with local setup, Service Bus opt-in, privacy rules, and future Azure Monitor boundary.

## Tests and validation

- [x] Add focused Azure-independent trace and metric-cardinality tests.
- [x] Restore packages and build the solution with zero warnings or errors.
- [x] Run the Azure-independent unit suite.
- [x] Run PostgreSQL Testcontainers integration tests.
- [x] Validate the OpenSpec change and archive it after final validation.
