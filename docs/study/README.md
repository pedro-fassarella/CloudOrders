# Estudo do CloudOrders

Esta série acompanha a construção do CloudOrders como um sistema distribuído pequeno, mas real o bastante para tornar visíveis persistência, mensageria, falhas e observabilidade. O material usa o repositório como referência principal: distingue o walking skeleton original do estado atual e explica por que cada capacidade foi adicionada em uma etapa diferente.

A leitura recomendada é incremental. Primeiro entenda as fronteiras e a persistência; depois avance para o transporte de mensagens e os consumidores; por fim estude as garantias de confiabilidade, a observabilidade, o Outbox e a visão integrada do sistema.

## Recommended reading order

1. [01 — Walking skeleton e arquitetura](./01-walking-skeleton-and-architecture.md) apresenta o menor caminho vertical que o projeto executou no início: HTTP, Application, Domain, Infrastructure e PostgreSQL. Também explica limites de projeto, direção das dependências e a diferença entre uma fronteira útil e overengineering.

2. [02 — PostgreSQL, Docker e Testcontainers](./02-postgresql-docker-and-testcontainers.md) mostra por que PostgreSQL foi a fundação e como `CloudOrdersDbContext`, EF Core, migrations explícitas, Docker Compose e Testcontainers se complementam. O foco é entender a diferença entre desenvolvimento manual persistente e integração automatizada isolada.

3. [03 — Azure Service Bus: pub/sub](./03-azure-service-bus-pub-sub.md) introduz a escolha de topic e subscriptions em vez de uma queue compartilhada. O capítulo usa `order-events`, o messaging probe, metadata nativo, filtros SQL, PeekLock e o requisito do tier Standard como exemplos concretos.

4. [04 — `OrderCreated` e fluxo event-driven](./04-order-created-and-event-driven-flow.md) descreve o contrato de integração e o fluxo inicial de persistir o pedido e publicar o evento. Explica status explicitamente mapeado, identidades de correlação e o dual-write que motivou a evolução posterior para Outbox.

5. [05 — Workers e fan-out](./05-workers-and-fan-out.md) acompanha Payment, Inventory e Notification como consumidores independentes da mesma publicação. A leitura separa o wrapper de Service Bus do processor de negócio e mostra PeekLock, settlement explícito, processamento serial inicial e simulações determinísticas.

6. [06 — Consumers idempotentes e Inbox](./06-idempotent-consumers-and-inbox.md) explica por que at-least-once pode gerar redelivery e como `processed_messages` evita efeitos duplicados. A chave `(ConsumerName, MessageId)`, a claim atômica no PostgreSQL e as janelas de crash são o centro do capítulo.

7. [07 — Retry e Dead Letter](./07-retry-and-dead-letter.md) organiza as falhas em contract, business-rule, transient e settlement/transport. O capítulo mostra quando deixar a mensagem unsettled, quando enviar imediatamente à DLQ e como o limite de `MaxDeliveryCount = 5`, configurado no broker, limita poison messages sem introduzir Polly ou retry local.

8. [08 — Observabilidade com OpenTelemetry](./08-observability-with-opentelemetry.md) conecta logs, traces e metrics aos hosts da solução. Explica Resource, `service.name`, Activities, instruments, identidades técnicas e de negócio, cardinalidade, exporters e a leitura de incidentes de PostgreSQL e Service Bus.

9. [09 — Transactional Outbox](./09-transactional-outbox.md) apresenta a solução atual para o dual-write: Order e intenção de publicação em `outbox_messages` dentro da mesma transação PostgreSQL. O dispatcher, o `MessageId` estável, o estado pending/published e a duplicata possível após um crash são relacionados ao Inbox já existente.

10. [10 — Arquitetura end-to-end](./10-cloudorders-end-to-end-architecture.md) é o capítulo de síntese. Ele percorre um pedido completo, reúne garantias e não-garantias, compara os patterns, discute falhas e registra o que o futuro deploy-to-Azure deve acrescentar sem apagar as fronteiras atuais.

## Mapa conceitual

```text
                  persistência local
        Order + PostgreSQL + migrations explícitas
                         |
                         | mesma transação
                         v
                  Transactional Outbox
              (intenção + metadata + payload)
                         |
                         | dispatcher / publish eventual
                         v
             Azure Service Bus: order-events
                         |
              +----------+----------+
              |                     |
              v                     v
       Payment subscription   Inventory subscription   Notification subscription
              |                     |                     |
              +----------+----------+----------+
                         v
              Workers + Inbox/idempotency
                         |
 falha transitória -> redelivery pelo broker -> DLQ após o limite

  OpenTelemetry atravessa API, PostgreSQL, Outbox, broker e workers:
  logs + traces + metrics explicam o caminho e seus outcomes.
```

O mapa também ajuda a separar problemas: persistência garante o estado local; Outbox protege a intenção de publicar; topic/subscriptions fazem fan-out; Inbox protege o processamento repetido de cada consumer; retry/DLQ administra entregas que falham; observabilidade torna o comportamento investigável.

## Suggested learning path

Para estudar em incrementos, use quatro passagens:

- **Fundação:** leia os capítulos 01 e 02 e consiga explicar o fluxo original e por que o banco, o schema e as fronteiras são decisões arquiteturais.
- **Distribuição:** leia 03, 04 e 05. Desenhe de memória o topic `order-events`, suas três subscriptions e o caminho de `OrderCreated` até cada worker.
- **Confiabilidade:** leia 06, 07 e 09. Para cada falha, responda qual estado fica durável, quem tenta novamente, quando ocorre settlement e qual chave torna a repetição segura.
- **Operação e síntese:** leia 08 e 10. Refaça um incidente usando `OrderId`, `MessageId`, `CorrelationId` e `TraceId`, depois compare as garantias locais com as não-garantias distribuídas.

Uma boa prática é estudar cada capítulo com o repositório aberto: procure os nomes de projetos, interfaces, tabelas e opções mencionados; em seguida tente explicar o comportamento sem confundir estado inicial com evolução atual.

## Quick revision before interviews

- **Arquitetura e dependências:** capítulos [01](./01-walking-skeleton-and-architecture.md) e [10](./10-cloudorders-end-to-end-architecture.md).
- **PostgreSQL, EF Core e migrations:** capítulos [02](./02-postgresql-docker-and-testcontainers.md) e [09](./09-transactional-outbox.md).
- **Queues, topics e fan-out:** capítulos [03](./03-azure-service-bus-pub-sub.md), [04](./04-order-created-and-event-driven-flow.md) e [05](./05-workers-and-fan-out.md).
- **Duplicatas, idempotência e Inbox:** capítulos [06](./06-idempotent-consumers-and-inbox.md) e [09](./09-transactional-outbox.md).
- **Retry, settlement e DLQ:** capítulos [05](./05-workers-and-fan-out.md), [06](./06-idempotent-consumers-and-inbox.md) e [07](./07-retry-and-dead-letter.md).
- **Logs, traces e metrics:** capítulo [08](./08-observability-with-opentelemetry.md), com a visão integrada no [10](./10-cloudorders-end-to-end-architecture.md).
- **Trade-offs e produção:** capítulos [09](./09-transactional-outbox.md) e [10](./10-cloudorders-end-to-end-architecture.md).

Este índice documenta o projeto implementado hoje, não uma promessa de funcionalidades futuras. O material deve evoluir quando CloudOrders ganhar deployment Azure, CI/CD e IaC; essas etapas poderão acrescentar infraestrutura e operação, mas devem ser avaliadas contra as fronteiras, contratos e garantias descritos aqui.
