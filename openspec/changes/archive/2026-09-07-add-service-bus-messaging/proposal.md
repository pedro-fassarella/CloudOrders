# Adicionar Mensageria Azure Service Bus

## Objetivo

Introduzir a capacidade base de mensageria do CloudOrders em .NET 10, provando o fluxo publisher -> Azure Service Bus Topic -> Subscription -> consumer -> completion explicito com uma mensagem temporaria de probe.

## Motivacao

O walking skeleton atual comprova HTTP, Application, Domain e PostgreSQL, mas ainda nao possui uma fronteira de mensageria. Esta mudanca estabelece essa fronteira sem antecipar o evento real `OrderCreated`, efeitos de negocio ou infraestrutura de producao.

## Escopo

- Porta `IMessagePublisher` na Application, sem tipos Azure.
- Publisher Azure Service Bus na Infrastructure usando `Azure.Messaging.ServiceBus` e `System.Text.Json`.
- Endpoint temporario Development-only `POST /messaging/probe`.
- Projeto `CloudOrders.Messaging.Worker` para consumir a subscription e completar a mensagem apos processamento bem-sucedido.
- Topic `order-events`, subscription `messaging-probe` e filtro exclusivo do probe documentados para criacao manual em Azure Service Bus Standard.
- Configuracao local por User Secrets ou environment variables, sem credenciais versionadas.
- Testes unitarios sem namespace Azure e smoke test Azure separado.
- ADR para a decisao Topic/Subscription.

## Fora de escopo

- `OrderCreated`, alteracao de `POST /orders`, Payment/Inventory/Notification Workers, contratos de producao e projeto Contracts.
- Idempotencia, retry/DLQ, Outbox, observabilidade adicional, health check de Service Bus, deployment Azure, Managed Identity implementada, Key Vault, IaC e CI/CD.

## Criterios de sucesso

1. Em Development, `POST /messaging/probe` publica uma mensagem JSON no topic configurado e retorna seus identificadores.
2. O worker consome a copia da subscription, registra os metadados e a completa explicitamente somente apos desserializacao e processamento bem-sucedidos.
3. `POST /orders` continua sem publicar eventos.
4. `dotnet test` continua independente de um namespace Azure e os testes PostgreSQL Testcontainers permanecem inalterados.
5. README documenta recursos minimos, configuracao local segura e smoke test real separado.
