# Design: Payment Worker

## Flow and boundaries

`POST /orders` remains unchanged: it persists the order and publishes `OrderCreated` through the existing Application publisher port to `order-events`. Azure Service Bus creates a copy for the dedicated `payment` subscription. `CloudOrders.Payment.Worker` receives that copy, passes primitive message data to `PaymentMessageProcessor`, then supplies `ProcessMessageEventArgs.CompleteMessageAsync` as the settlement callback.

The new worker references Application for `OrderCreated` and Infrastructure for the existing Service Bus client/options registration. Azure SDK processor concerns stay in the hosted worker; `PaymentMessageProcessor` has no Azure SDK dependency and accepts payload bytes, native metadata, and a completion delegate. No consumer port, Contracts project, or generic messaging framework is introduced.

## Subscription and processing

The topic remains `order-events`. Azure setup creates subscription `payment`, deletes `$Default`, and adds `order-created-only` with SQL filter `sys.Label = 'CloudOrders.Orders.OrderCreated'`. The existing `messaging-probe` subscription remains unchanged.

The worker configures `ServiceBusProcessor` for explicit Peek-Lock handling, `AutoCompleteMessages = false`, and `MaxConcurrentCalls = 1`. It validates subject and content type, deserializes with `JsonSerializerDefaults.Web`, awaits payment processing, then completes the message. Exceptions propagate to the processor's existing default behavior; this change contains no explicit abandon, retry, DLQ, or idempotency handling.

## Contract versus business validation

Unexpected subject, content type, empty body, or invalid JSON are message-contract failures. They occur before payment processing and are not settled.

Deserialization does not enforce the order status. `SimulatedPaymentProcessor` is the business boundary: it supports the current `Pending` order flow and returns `simulated-payment-{OrderId:N}` without delay, I/O, database access, or a gateway. An unsupported status raises an explicit payment-entry business/contract exception, is not a transport or deserialization failure, and is not settled.

## Diagnostics, configuration, and tests

Successful processing and completion log OrderId, MessageId, and CorrelationId; successful processing also logs the deterministic payment reference. The worker uses `ConnectionStrings:ServiceBus`, `Messaging:TopicName`, and `Messaging:SubscriptionName`, with safe committed defaults and User Secrets guidance.

Unit tests use the Azure-free message-processor seam and fake payment/completion delegates to verify contract deserialization, status-boundary behavior, deterministic processing, and completion ordering. A manual Azure smoke test creates the filtered subscription, starts API and worker, posts an order, verifies logs, and confirms the subscription has no active message.
