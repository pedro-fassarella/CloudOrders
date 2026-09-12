# Health API Specification

## GET /health

`GET /health` usa os health checks padrao do ASP.NET Core e inclui a conectividade de `CloudOrdersDbContext` com PostgreSQL.

Quando o banco estiver acessivel, retorna `200 OK`. Quando a dependencia PostgreSQL nao puder ser acessada, o mecanismo padrao reporta unhealthy com `503 Service Unavailable`.

Nenhum health check de Azure Service Bus ou outra dependencia externa e adicionado.
