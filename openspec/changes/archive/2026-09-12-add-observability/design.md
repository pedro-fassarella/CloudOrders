# Design: Observability

## Architecture and propagation

`CloudOrders.Application` owns the provider-neutral `CloudOrders` `ActivitySource` and `Meter`. The API creates `cloudorders.order.create` around persistence and publishing. Each business consumer creates `cloudorders.messaging.process` as an application child span around validation, inbox processing, and settlement. `OrderId`, native `MessageId`, and native `CorrelationId` are activity tags and structured-log properties; W3C trace IDs remain technical trace context.

The Infrastructure registration configures the `CloudOrders` source/meter, Npgsql tracing and metrics, EF Core metrics, runtime metrics, ASP.NET Core instrumentation for the API, and the exact Service Bus SDK source `Azure.Messaging.ServiceBus.Message`. The three business workers use distinct resource service names. The messaging probe worker is intentionally excluded.

## Azure Service Bus gate

The resolved `Azure.Messaging.ServiceBus` 7.20.2 package XML states that the ActivitySource is experimental and controlled by its feature flag. The implementation therefore uses no `AppContext` switch. Service Bus transport spans are available only when `AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE=true` is present before startup. This preserves SDK diagnostic-context propagation while avoiding a legacy or unconditional opt-in.

## Logs and metrics

Console logs use the JSON formatter and scopes/structured fields for `Service`, `Operation`, `OrderId`, `MessageId`, `CorrelationId`, `TraceId`, `SpanId`, `Consumer`, `Outcome`, and `DeliveryCount` where known. Logs and telemetry never contain message bodies, customer payloads, credentials, connection strings, or SQL parameter values.

The custom meter records persisted/published orders; consumer processing count/duration/outcome; dead letters; settlement results; redeliveries; and processor errors. Only consumer, outcome, settlement, reason, and error-source dimensions are allowed. Business IDs and exception text are excluded from metric attributes.

## Exporters and testing

Development configuration selects console traces and metrics. `Observability:Exporter=Otlp` selects the OTLP exporter and uses standard `OTEL_EXPORTER_OTLP_*` configuration. Non-Development environments default to no exporter, so integration tests have no external dependency. Future Azure Monitor integration is a configuration/package addition at the exporter boundary; it does not alter application instrumentation.
