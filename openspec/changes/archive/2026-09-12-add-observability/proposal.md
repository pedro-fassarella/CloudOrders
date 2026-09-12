# Add Observability

## Objective

Make one order traceable from `POST /orders`, through PostgreSQL persistence and `OrderCreated` publication, to Azure Service Bus processing by the Payment, Inventory, and Notification Workers.

## Scope

- Add shared application `ActivitySource` and `Meter` definitions.
- Instrument API order creation, Npgsql/EF Core persistence, Azure Service Bus transport, and business-worker processing outcomes.
- Use structured JSON console logging with business and technical correlation identifiers.
- Provide a Development console exporter and opt-in OTLP exporter.
- Keep automated tests independent of Azure and external telemetry backends.
- Document future Azure Monitor integration without adding Azure deployment configuration.

## Non-goals

- Outbox, request idempotency, retry, settlement, or dead-letter behavior changes.
- Azure deployment, IaC, CI/CD, dashboards, alerts, or a local collector.
- Message-schema or business-functionality changes.
- Instrumentation of the independent messaging probe worker.

## Success criteria

1. The API creates an application trace with `OrderId`, `MessageId`, and `CorrelationId` after successful order creation.
2. Each business worker records a correlated processing span, outcome metric, and structured lifecycle logs without recording message bodies or secrets.
3. Service Bus send/process/settlement spans are enabled only through the supported environment opt-in required by the repository-resolved SDK; no `AppContext` switch is added.
4. Unit and integration tests run without Azure credentials or a telemetry backend.

## OpenSpec lifecycle

The change-local proposal, design, tasks, and observability specification are completed with the implementation. The canonical specification is promoted and this change is archived only after validation.
