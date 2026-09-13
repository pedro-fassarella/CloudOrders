# CloudOrders

CloudOrders is a .NET 10 walking skeleton for an event-driven order-processing project. It proves the HTTP, application, domain and PostgreSQL persistence flow, durably dispatches the real `OrderCreated` integration event through Azure Service Bus, and retains a Development-only messaging probe for transport diagnostics.

## Prerequisites

- .NET SDK 10.0.301 or a compatible .NET 10 SDK
- Visual Studio with the .NET 10 workload
- Docker Desktop or another Docker-compatible runtime
- Azure CLI and an Azure subscription, only when running the Service Bus smoke test

## Local development

The normal local workflow runs PostgreSQL in Docker Compose and runs `CloudOrders.Api` from Visual Studio.

### Start PostgreSQL

Copy the safe example configuration, then replace the password placeholder with a local-only value before PostgreSQL initializes its data volume for the first time:

```powershell
Copy-Item .env.example .env
# Edit .env and replace POSTGRES_PASSWORD=replace-with-a-local-password.
docker compose up -d postgres
```

Check the rendered configuration, service status, and logs:

```powershell
docker compose config
docker compose ps
docker compose logs postgres
```

The default host port is `5432`. Set `POSTGRES_PORT` in `.env` if that port is already in use, and use the same value in the API connection string.

PostgreSQL applies `POSTGRES_DB`, `POSTGRES_USER`, and `POSTGRES_PASSWORD` only when it initializes an empty volume. Changing those values after `cloudorders_pg` has been initialized does not reconfigure the existing database.

### Configure the API

Use User Secrets for the API connection string; do not add credentials to committed appsettings files. Replace the placeholder values with the values from your `.env` file:

```powershell
dotnet user-secrets set --project src/CloudOrders.Api "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=cloudorders;Username=cloudorders;Password=your-local-password"
```

The standard .NET configuration order is committed appsettings, then User Secrets in Development, then environment variables. `ConnectionStrings__Postgres` can therefore override the User Secret when needed. `.env` is consumed only by Docker Compose; the application does not parse it.

### Configure Service Bus messaging

`POST /orders` commits the order and a pending `OrderCreated` outbox message in one PostgreSQL transaction. The API-hosted Outbox Dispatcher later publishes the stored JSON message to topic `order-events`. `CloudOrders.Payment.Worker`, `CloudOrders.Inventory.Worker`, and `CloudOrders.Notification.Worker` consume independent filtered copies from `payment`, `inventory`, and `notification`: Payment performs deterministic simulated payment, Inventory performs deterministic simulated reservation, and Notification performs deterministic simulated notification for the current `Pending` flow. Each logs `OrderId`, `MessageId`, and `CorrelationId`, logs its deterministic reference, and explicitly completes only its successful subscription message. The Development-only `POST /messaging/probe` endpoint and `CloudOrders.Messaging.Worker` remain independent transport diagnostics on `messaging-probe`.

This setup uses a real Azure Service Bus namespace. It is separate from Docker Compose, and no Service Bus secret belongs in `.env` or committed appsettings.

#### Create development resources

Choose a globally unique namespace name, sign in with `az login`, then create one Standard namespace, topic, and filtered subscriptions:

```powershell
$resourceGroup = "rg-cloudorders-dev"
$location = "brazilsouth"
$namespace = "sb-cloudorders-dev-unique-suffix"
$topic = "order-events"
$probeSubscription = "messaging-probe"
$paymentSubscription = "payment"
$inventorySubscription = "inventory"
$notificationSubscription = "notification"
$maxDeliveryCount = 5

az group create --name $resourceGroup --location $location
az servicebus namespace create --resource-group $resourceGroup --name $namespace --location $location --sku Standard
az servicebus topic create --resource-group $resourceGroup --namespace-name $namespace --name $topic
az servicebus topic subscription create --resource-group $resourceGroup --namespace-name $namespace --topic-name $topic --name $probeSubscription
az servicebus topic subscription rule delete --resource-group $resourceGroup --namespace-name $namespace --topic-name $topic --subscription-name $probeSubscription --name '$Default'
az servicebus topic subscription rule create --resource-group $resourceGroup --namespace-name $namespace --topic-name $topic --subscription-name $probeSubscription --name probe-only --filter-sql-expression "sys.Label = 'CloudOrders.Messaging.Probe'"
az servicebus topic subscription create --resource-group $resourceGroup --namespace-name $namespace --topic-name $topic --name $paymentSubscription --max-delivery-count $maxDeliveryCount
az servicebus topic subscription rule delete --resource-group $resourceGroup --namespace-name $namespace --topic-name $topic --subscription-name $paymentSubscription --name '$Default'
az servicebus topic subscription rule create --resource-group $resourceGroup --namespace-name $namespace --topic-name $topic --subscription-name $paymentSubscription --name order-created-only --filter-sql-expression "sys.Label = 'CloudOrders.Orders.OrderCreated'"
az servicebus topic subscription create --resource-group $resourceGroup --namespace-name $namespace --topic-name $topic --name $inventorySubscription --max-delivery-count $maxDeliveryCount
az servicebus topic subscription rule delete --resource-group $resourceGroup --namespace-name $namespace --topic-name $topic --subscription-name $inventorySubscription --name '$Default'
az servicebus topic subscription rule create --resource-group $resourceGroup --namespace-name $namespace --topic-name $topic --subscription-name $inventorySubscription --name order-created-only --filter-sql-expression "sys.Label = 'CloudOrders.Orders.OrderCreated'"
az servicebus topic subscription create --resource-group $resourceGroup --namespace-name $namespace --topic-name $topic --name $notificationSubscription --max-delivery-count $maxDeliveryCount
az servicebus topic subscription rule delete --resource-group $resourceGroup --namespace-name $namespace --topic-name $topic --subscription-name $notificationSubscription --name '$Default'
az servicebus topic subscription rule create --resource-group $resourceGroup --namespace-name $namespace --topic-name $topic --subscription-name $notificationSubscription --name order-created-only --filter-sql-expression "sys.Label = 'CloudOrders.Orders.OrderCreated'"
```

For subscriptions that already exist, pin the same broker delivery limit explicitly:

```powershell
az servicebus topic subscription update --resource-group $resourceGroup --namespace-name $namespace --topic-name $topic --name $paymentSubscription --max-delivery-count $maxDeliveryCount
az servicebus topic subscription update --resource-group $resourceGroup --namespace-name $namespace --topic-name $topic --name $inventorySubscription --max-delivery-count $maxDeliveryCount
az servicebus topic subscription update --resource-group $resourceGroup --namespace-name $namespace --topic-name $topic --name $notificationSubscription --max-delivery-count $maxDeliveryCount
```

Basic tier does not support topics and subscriptions, so this stage requires Standard. The probe filter excludes `OrderCreated`; the payment, inventory, and notification filters each receive an independent `CloudOrders.Orders.OrderCreated` copy.

For local development, create a namespace Shared Access Signature policy with only Send and Listen rights, retrieve its connection string into a shell variable, and store it in User Secrets for each executable that uses Service Bus:

```powershell
az servicebus namespace authorization-rule create --resource-group $resourceGroup --namespace-name $namespace --name cloudorders-local --rights Send Listen
$serviceBusConnectionString = az servicebus namespace authorization-rule keys list --resource-group $resourceGroup --namespace-name $namespace --name cloudorders-local --query primaryConnectionString --output tsv

dotnet user-secrets set --project src/CloudOrders.Api "ConnectionStrings:ServiceBus" "$serviceBusConnectionString"
dotnet user-secrets set --project src/CloudOrders.Messaging.Worker "ConnectionStrings:ServiceBus" "$serviceBusConnectionString"
dotnet user-secrets set --project src/CloudOrders.Payment.Worker "ConnectionStrings:ServiceBus" "$serviceBusConnectionString"
dotnet user-secrets set --project src/CloudOrders.Inventory.Worker "ConnectionStrings:ServiceBus" "$serviceBusConnectionString"
dotnet user-secrets set --project src/CloudOrders.Notification.Worker "ConnectionStrings:ServiceBus" "$serviceBusConnectionString"
```

Payment, Inventory, and Notification also use PostgreSQL for their shared durable processed-message inbox. Configure the same local PostgreSQL connection string used by the API for each business worker:

```powershell
dotnet user-secrets set --project src/CloudOrders.Payment.Worker "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=cloudorders;Username=cloudorders;Password=your-local-password"
dotnet user-secrets set --project src/CloudOrders.Inventory.Worker "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=cloudorders;Username=cloudorders;Password=your-local-password"
dotnet user-secrets set --project src/CloudOrders.Notification.Worker "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=cloudorders;Username=cloudorders;Password=your-local-password"
```

The inbox uses the native Service Bus `MessageId` plus a centralized logical consumer identity. After a worker has committed a message as processed, a redelivery for that same worker skips the simulation and is completed safely. Payment, Inventory, and Notification each retain an independent inbox key for the same event. This protects the current deterministic local simulations; it is not a distributed exactly-once guarantee for future external providers.

`Messaging:TopicName` and `Messaging:SubscriptionName` have safe committed defaults. The API also configures `Outbox:Enabled=true`, `Outbox:BatchSize=20`, and `Outbox:PollingIntervalSeconds=5`; environment variables such as `Outbox__BatchSize` override them. Initial deployment supports exactly one active Outbox Dispatcher instance. When the API is scaled, enable the dispatcher on one replica only and set `Outbox:Enabled=false` on every other replica. Stable MessageIds and idempotent consumers make duplicate delivery safe, but they do not coordinate concurrent dispatchers.

`ConnectionStrings__ServiceBus`, `Messaging__TopicName`, and `Messaging__SubscriptionName` can override Service Bus settings through environment variables. Managed Identity with Azure Service Bus RBAC is the intended deployment direction, but is not implemented in this increment.

### Apply migrations

Migrations remain an explicit developer action:

```powershell
dotnet ef database update --project src/CloudOrders.Infrastructure --startup-project src/CloudOrders.Api -- --environment Development
```

The API does not run `Database.Migrate()` on startup, and Compose does not initialize the application schema.

### Run and verify

Set `CloudOrders.Api` as the startup project in Visual Studio and run an existing Development profile. The HTTP profile listens on `http://localhost:5049`; the HTTPS profile also listens on `https://localhost:7172`.

`dotnet run --project src/CloudOrders.Api --launch-profile http` is an equivalent command-line option.

```powershell
Invoke-RestMethod http://localhost:5049/health
$order = Invoke-RestMethod -Method Post -Uri http://localhost:5049/orders -ContentType application/json -Body '{"customerId":"customer-123"}'
Invoke-RestMethod "http://localhost:5049/orders/$($order.id)"
```

On successful `POST /orders`, the API returns `201 Created` after the order and its `OrderCreated` outbox row have committed together. It does not wait for Service Bus. The dispatcher later uses the persisted subject `CloudOrders.Orders.OrderCreated`, content type `application/json`, stable native `MessageId`, and created order ID as native `CorrelationId`. Its JSON payload contains only `orderId`, `customerId`, `status`, and `createdAtUtc`; `status` is the stable integration value `Pending`, not a serialized domain enum.

### Observability

The API and Payment, Inventory, and Notification Workers emit JSON console logs with structured `Service`, `Operation`, `OrderId`, `MessageId`, `CorrelationId`, `TraceId`, `SpanId`, `Consumer`, `Outcome`, and `DeliveryCount` properties where each value is available. The API adds outbox persistence and dispatch spans, persists W3C trace context as outbox metadata rather than business payload, and emits low-cardinality pending and dispatch-attempt metrics. Message bodies, customer payloads, credentials, connection strings, exception messages, and SQL parameter values are not logged or attached as telemetry attributes.

Development defaults to the console trace and metrics exporter. Set `Observability__Exporter=Otlp` and the standard `OTEL_EXPORTER_OTLP_*` variables to send telemetry to a developer-provided local collector; `Observability__Exporter=None` disables exporters and is the default outside Development, including automated integration tests. No collector, dashboard, or alerting stack is included in Docker Compose.

The repository resolves `Azure.Messaging.ServiceBus` 7.20.2. Its package documentation confirms that its ActivitySource remains experimental and feature-flag gated, so include SDK-level send/process/settlement spans by setting the documented environment variable before starting the API or business workers:

```powershell
$env:AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE = "true"
```

No `AppContext` switch is used. When that variable is absent, CloudOrders application spans and metrics continue to work, but Service Bus SDK transport spans are not expected. A future Azure deployment can replace the local exporter configuration with Azure Monitor OpenTelemetry configuration without changing business or message-processing code.

### Transactional outbox dispatch

The outbox row stores the serialized `OrderCreated` payload, native Service Bus metadata, stable MessageId, attempt metadata, and optional W3C trace context in the same transaction as the Order. The dispatcher sends pending rows oldest first, marks them published only after Service Bus confirms the send, and does not delete published rows.

If sending fails, the row remains pending for a later polling attempt. If Service Bus confirms a send but the database update fails or the process stops first, the row remains pending and can be sent again with the same MessageId. Payment, Inventory, and Notification use their durable consumer-scoped inbox records to skip duplicate business execution safely. This is at-least-once publication, not distributed exactly-once delivery.

This initial dispatcher deliberately has no PostgreSQL claim, lease, `FOR UPDATE SKIP LOCKED`, or row-lock coordination. Run one active dispatcher instance only. Multi-instance dispatcher coordination, cleanup/retention, retry limits, poison handling, and replay remain future work.

### Run the Service Bus smoke flow

After configuring the Service Bus User Secrets, start Payment, Inventory, and Notification Workers in separate terminals:

```powershell
dotnet run --project src/CloudOrders.Payment.Worker -- --environment Development
```

```powershell
dotnet run --project src/CloudOrders.Inventory.Worker -- --environment Development
```

```powershell
dotnet run --project src/CloudOrders.Notification.Worker -- --environment Development
```

With the API running in Development, create an order:

```powershell
$order = Invoke-RestMethod -Method Post -Uri http://localhost:5049/orders -ContentType application/json -Body '{"customerId":"customer-123"}'
```

Confirm that the Payment Worker logs the returned order ID, native `MessageId`, `CorrelationId`, deterministic `simulated-payment-...` reference, and completion. Confirm that the Inventory Worker logs the same order ID and correlation ID, its `simulated-reservation-...` reference, and completion. Confirm that the Notification Worker logs the same order ID, message metadata, `simulated` channel, `simulated-notification-...` reference, and completion. Use Service Bus Explorer or Azure portal subscription metrics to confirm that `payment`, `inventory`, and `notification` have no active message after completion; this proves independent topic fan-out for one `OrderCreated` event.

All business workers reject an unexpected subject, content type, empty body, invalid JSON, or missing native `MessageId` as a permanent message-contract failure. They immediately dead-letter those messages with reason `MessageContractViolation`. JSON deserialization is independent of business status. Their current simulations support `Pending`; another status is an unsupported business-status failure, not a transport or deserialization failure, and is immediately dead-lettered with reason `UnsupportedBusinessStatus`.

Dead-letter descriptions identify the consumer, native message ID, exception type, and concise failure detail; they do not include message bodies. The business subscriptions use `MaxDeliveryCount = 5`. Transient business, PostgreSQL/inbox, and unknown processing failures are neither completed nor manually dead-lettered: the Service Bus processor abandons the unsettled delivery and the broker redelivers it. After five unsuccessful deliveries, Service Bus moves it to the subscription DLQ with `MaxDeliveryCountExceeded`. Completion, dead-letter settlement, lock-loss, and other transport failures remain transport failures; they are not converted into a permanent message failure and receive no Polly or application retry loop.

### Retry and DLQ smoke flow

With the three workers running, use Service Bus Explorer to send a message to `order-events` with subject `CloudOrders.Orders.OrderCreated`, content type `application/json`, and a malformed body such as `not-json`. Confirm that each business subscription gets one DLQ message with `MessageContractViolation` and a description that names its consumer and message ID. Repeat with valid `OrderCreated` JSON whose status is `Cancelled` and confirm `UnsupportedBusinessStatus`.

To observe broker redelivery, temporarily stop local PostgreSQL after the workers are running, publish one valid `Pending` order, and restore PostgreSQL before the fifth delivery; the message should eventually complete without duplicate business execution. In a separate controlled run, keep PostgreSQL unavailable and confirm the affected subscription DLQ entry has broker reason `MaxDeliveryCountExceeded`. This is a developer-run Azure smoke test, not an automated test.

To check the subscription isolation, optionally start `CloudOrders.Messaging.Worker`, publish `POST /messaging/probe`, and confirm that only the probe worker logs and completes that message.

Endpoints:

- `GET /health`
- `POST /orders` with `{ "customerId": "customer-123" }`
- `GET /orders/{id}`
- Development-only `POST /messaging/probe`
- Development OpenAPI: `/openapi/v1.json`

### Stop or reset PostgreSQL

Stop the service while retaining local database data:

```powershell
docker compose down
```

After starting it again with `docker compose up -d postgres`, the named volume preserves the database and its Orders.

To deliberately discard the local database and reinitialize it with the current `.env` values:

```powershell
docker compose down -v
```

Warning: `docker compose down -v` deletes the local PostgreSQL volume and all local development data.

## Tests

Unit tests do not require PostgreSQL:

```powershell
dotnet test tests/CloudOrders.UnitTests
```

Integration tests start an isolated PostgreSQL 17 container through Testcontainers, apply the existing migration, and remove it after the suite. They do not require `docker compose up`, a local connection string, or `POSTGRES_*` variables:

```powershell
dotnet test tests/CloudOrders.IntegrationTests
```

Messaging serialization, outbox dispatcher, payment, inventory, notification processing, and order persistence tests do not require Azure. The end-to-end Service Bus check is the documented simultaneous Payment/Inventory/Notification smoke flow above and requires a developer-provisioned namespace; it is intentionally separate from normal automated tests.

Real email or SMS providers, notification persistence, DLQ replay/recovery tooling, provider-specific retry policy, a real payment gateway, real inventory system, payment or inventory persistence, multi-instance outbox coordination, outbox cleanup/retention, broker duplicate detection, Azure deployment, Key Vault, Managed Identity implementation, IaC, API containerization, production dashboards or alerts, and CI/CD remain deferred to future OpenSpec changes.
