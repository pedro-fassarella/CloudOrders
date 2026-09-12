# Bootstrap CloudOrders Walking Skeleton

## Objetivo

Estabelecer a menor arquitetura executável do CloudOrders em .NET 10, provando o fluxo HTTP -> aplicação -> domínio -> persistência -> resposta sem antecipar mensageria ou infraestrutura Azure.

## Motivação

O plano legado descreve o estado final do projeto, mas o repositório ainda não possui uma solution ou código. Esta mudança cria uma base pequena, testável e evolutiva para as próximas mudanças OpenSpec.

## Escopo

- Solution .NET 10 com API, domínio, aplicação, infraestrutura e dois projetos de teste.
- `GET /health` usando health checks padrão.
- `POST /orders` com criação mínima de pedido.
- Persistência real em PostgreSQL usando EF Core 10.
- PostgreSQL local executado por Docker Compose.
- Testes unitários e testes de integração com `WebApplicationFactory`.
- README inicial e ADRs arquiteturalmente relevantes.

## Fora de escopo

Workers, Azure Service Bus, tópicos, subscriptions, retry, DLQ, idempotência, Outbox, OpenTelemetry, Application Insights, Container Apps, ACR, CI/CD, IaC, autenticação e regras completas de pedido.

## Critérios de sucesso

1. A solution restaura e compila com .NET 10.
2. `GET /health` retorna HTTP 200 sem depender do banco.
3. `POST /orders` valida `customerId`, persiste um pedido `Pending` e retorna HTTP 201.
4. Testes unitários e de integração estão presentes e documentados.
5. Nenhum segredo é versionado.

## Decisões adiadas

O retorno assíncrono `202 Accepted`, eventos, workers e o modelo completo de itens ficam para mudanças posteriores. A rota desta mudança é `/orders`, seguindo o prompt atual em vez do `/api/orders` do documento legado.
