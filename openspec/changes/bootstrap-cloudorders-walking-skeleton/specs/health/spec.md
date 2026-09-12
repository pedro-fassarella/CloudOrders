# Health API Specification

## GET /health

The API MUST expose `/health` through ASP.NET Core health checks.

When the API process is running, the endpoint MUST return `200 OK` with the standard healthy response. This first health check MUST NOT require PostgreSQL, Service Bus or any external dependency.
