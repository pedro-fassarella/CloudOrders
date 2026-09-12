# ADR 0004 Azure Service Bus Topics and Subscriptions

## Status

Accepted

## Context

CloudOrders is moving from a synchronous walking skeleton toward event-driven processing. The supplied Azure reference plan suggests beginning with a queue for study purposes, but the current project objective is to establish a topic and subscription flow that can later fan out independent integration-event consumers.

## Decision

Use Azure Service Bus Standard with topic `order-events` and subscription `messaging-probe` for the foundational messaging increment. The API publishes a Development-only temporary probe through an Application publisher port. A separate Messaging Worker consumes the probe from the subscription and explicitly completes it after successful JSON processing.

The real `OrderCreated` event and its business consumers remain separate future work. Local development uses a connection string in User Secrets; future Azure deployment should use Managed Identity and Azure Service Bus RBAC.

## Consequences

Topics preserve the intended publish/subscribe direction and allow future consumers to add independent subscriptions. This increment adds one temporary worker and does not introduce a generic messaging framework, outbox, idempotency, retry/DLQ policy, health check, or production Azure deployment.
