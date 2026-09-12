# Orders API Specification

## POST /orders

### Request

```json
{
  "customerId": "customer-123"
}
```

### Success

When `customerId` contains a non-empty value, the API MUST create an order with a new GUID, the current UTC timestamp and status `Pending`. It MUST persist the order before returning the response.

The response MUST be `201 Created` and contain:

```json
{
  "id": "00000000-0000-0000-0000-000000000000",
  "customerId": "customer-123",
  "status": "Pending",
  "createdAtUtc": "2026-09-07T12:00:00+00:00"
}
```

### Validation

When `customerId` is missing, empty or whitespace, the API MUST return `400 Bad Request` using `ProblemDetails` and MUST NOT attempt persistence.

### Deferred behavior

The endpoint does not publish messages, process payment or inventory, or return `202 Accepted` in this change.
