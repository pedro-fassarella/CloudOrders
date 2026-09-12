# Design: Retry and Dead-Letter Handling

## Failure classification and settlement

The Payment, Inventory, and Notification workers retain `AutoCompleteMessages = false`, Peek-Lock, and `MaxConcurrentCalls = 1`. They continue to complete only after successful processing or completed-duplicate detection.

The message processors receive two Azure-independent settlement delegates: the existing completion delegate and a new dead-letter delegate. They catch only `PermanentMessageFailureException` and use the dead-letter delegate with the following fixed mapping:

| Failure kind | Dead-letter reason |
| --- | --- |
| `ContractViolation` | `MessageContractViolation` |
| `UnsupportedBusinessStatus` | `UnsupportedBusinessStatus` |

Descriptions identify the logical consumer, native message ID, exception type, and concise failure message. They never include the message body and are limited to 4,096 characters.

Contract validation errors, empty bodies, deserialization failures, and missing message IDs are typed as `ContractViolation`. The existing unsupported-status exceptions derive from the shared permanent-failure exception and retain their consumer-specific status property.

All other exceptions, including business, inbox, database, cancellation, completion, dead-letter settlement, lock-loss, and receiver transport failures, propagate. No code calls `AbandonMessageAsync` or wraps Service Bus calls in Polly. The Service Bus processor abandons an unsettled handler exception; the SDK's built-in retry behavior applies to its own transient service operations before an exception reaches the handler.

## Idempotency ordering

1. Contract failures happen before `IProcessedMessageStore`; they create no inbox row and are dead-lettered immediately.
2. Unsupported status is thrown by the business callback inside `ExecuteOnceAsync`; its transaction rolls back, then the processor dead-letters the message.
3. Transient business or persistence failure rolls back or avoids `Completed`, propagates, and permits the same `(ConsumerName, MessageId)` to run again on redelivery.
4. Successful business execution commits the `Completed` inbox record before completion is attempted.
5. If completion fails after commit, the exception propagates. A later delivery sees `Completed`, skips business processing, and retries only completion.
6. If manual dead-letter settlement fails, no successful business completion exists; the exception propagates and broker redelivery retries the DLQ settlement attempt.

`IProcessedMessageStore`, the inbox schema, and the business processor interfaces remain unchanged.

## Broker retry bound

`MaxDeliveryCount = 5` is configured on the `payment`, `inventory`, and `notification` subscription entities, not in `ServiceBusProcessorOptions`. It limits recurring unsettled deliveries caused by transient processing or settlement/transport failure. When that threshold is reached, Service Bus moves the message to the subscription DLQ with `MaxDeliveryCountExceeded`.

The `messaging-probe` subscription remains unchanged.

## Tests and documentation

The existing delegate seam keeps unit tests Azure-independent. Tests record completion and dead-letter requests, use the in-memory processed-message store, and invoke a second delivery to prove retry and duplicate safety.

README documents the explicit subscription delivery limit, immediate DLQ reasons, and a developer-run smoke test that sends malformed and unsupported-status messages. A controlled PostgreSQL outage may be used to observe transient redelivery and the five-delivery broker fallback; it is not an automated Azure test.

## Deferred boundary

This design does not claim exactly-once execution for future external providers. Outbox, provider retry, DLQ replay, monitoring, deployment, and infrastructure automation remain deferred.
