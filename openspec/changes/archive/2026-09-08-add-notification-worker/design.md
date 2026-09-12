# Design: Notification Worker

## Flow and boundaries

`POST /orders` remains unchanged: after persistence it publishes the existing Application `OrderCreated` contract to `order-events`. Azure Service Bus creates independent copies for the `payment`, `inventory`, and `notification` subscriptions. `CloudOrders.Notification.Worker` receives the notification copy, passes primitive message data to `NotificationMessageProcessor`, and supplies `ProcessMessageEventArgs.CompleteMessageAsync` as the settlement callback.

The worker references Application for `OrderCreated` and Infrastructure for the existing Service Bus client/options registration. Azure SDK concerns remain in `ServiceBusNotificationWorker`; `NotificationMessageProcessor` has no Azure SDK dependency. No Contracts project, consumer port, generic messaging framework, or existing worker refactor is introduced.

## Subscription and processing

The topic remains `order-events`. Azure setup creates subscription `notification`, deletes `$Default`, and adds `order-created-only` with SQL filter `sys.Label = 'CloudOrders.Orders.OrderCreated'`. The existing `payment`, `inventory`, and `messaging-probe` subscriptions remain unchanged.

The worker configures `ServiceBusProcessor` for Peek-Lock handling, `AutoCompleteMessages = false`, and `MaxConcurrentCalls = 1`. It validates subject and content type, rejects an empty body, deserializes with `JsonSerializerDefaults.Web`, awaits notification processing, logs notification metadata, then completes the message. Exceptions propagate to the SDK processor behavior; there is no custom abandon, retry, DLQ, or idempotency handling.

## Contract, business, and transport failures

Unexpected subject, unexpected content type, an empty or null body, and malformed JSON are message-contract failures. They occur before notification processing and are not settled.

`SimulatedNotificationProcessor` is the business boundary. It supports the current `Pending` event flow and returns channel `simulated` with reference `simulated-notification-{OrderId:N}` without delay, I/O, persistence, or a real provider. An unsupported status raises `UnsupportedNotificationOrderStatusException`, is not a transport or JSON failure, and is not settled.

Transport and processor failures are logged by `ServiceBusNotificationWorker.ProcessErrorAsync`; they remain governed by the existing Service Bus processor behavior.

## Diagnostics, configuration, and tests

Successful notification logging contains OrderId, MessageId, CorrelationId, notification channel, and notification reference. Completion is logged after the completion callback returns successfully. The worker uses `ConnectionStrings:ServiceBus`, `Messaging:TopicName`, and `Messaging:SubscriptionName`, with safe committed defaults and User Secrets guidance.

Unit tests use the Azure-free message-processor seam and fake notification/completion delegates to verify contract validation, deterministic notification receipts, business-status behavior, failure without completion, exactly-once completion, and completion ordering. The manual Azure smoke test starts API, Payment Worker, Inventory Worker, and Notification Worker together, posts one order, verifies all independent subscription copies are completed, and confirms the probe subscription excludes the event.
