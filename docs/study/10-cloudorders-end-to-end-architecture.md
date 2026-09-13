# Arquitetura end-to-end do CloudOrders

O CloudOrders atual é um pequeno sistema distribuído. Um pedido começa em HTTP, atravessa o domínio e o PostgreSQL, deixa uma intenção durável de publicação, passa pelo Azure Service Bus e chega a três processos independentes. Cada etapa tem uma fronteira e uma forma própria de falhar.

Este capítulo monta o mapa inteiro. Desde o walking skeleton, o estado atual ganhou `OrderCreated`, topic/subscriptions, três workers, Inbox, retry/DLQ, OpenTelemetry e Transactional Outbox. Ainda não há providers reais, deployment Azure, múltiplos dispatchers coordenados, replay de DLQ ou migrations automáticas.

## O sistema em uma visão

```text
Cliente / ferramenta HTTP
          |
          | POST /orders
          v
 +---------------------------- CloudOrders.Api -----------------------------+
 | ASP.NET Core endpoint                                                     |
 |   CreateOrderHandler                                                      |
 |     Domain: Order.Create -> Pending                                       |
 |     Application: OrderCreated + MessageMetadata                           |
 |     Infrastructure: EF Core / Npgsql                                      |
 |              +--------------------+                                      |
 |              | PostgreSQL          |                                      |
 |              | orders              |                                      |
 |              | outbox_messages    |                                      |
 |              +--------------------+                                      |
 |                   COMMIT atomic                                          |
 |   -> 201 Created                                                          |
 |                                                                            |
 |   OutboxDispatcherHostedService (separate async loop)                     |
 |     IOutboxMessageStore -> OutboundMessage -> IMessagePublisher            |
 +------------------------------|---------------------------------------------+
                               |
                               | ServiceBusSender / topic
                               v
                    Azure Service Bus: order-events
                               |
          +--------------------+---------------------+
          |                    |                    |
          v                    v                    v
    subscription          subscription          subscription
       payment              inventory             notification
          |                    |                    |
          v                    v                    v
 +----------------+  +----------------+  +---------------------+
 | Payment Worker |  | Inventory      |  | Notification Worker |
 | ServiceBus     |  | Worker         |  | ServiceBus          |
 | wrapper        |  | ServiceBus     |  | wrapper             |
 | processor      |  | processor      |  | processor           |
 +-------+--------+  +-------+--------+  +----------+----------+
         |                   |                       |
         +-------------------+-----------------------+
                             |
                   PostgreSQL: processed_messages
                   key = (ConsumerName, MessageId)
                             |
                   CompleteMessageAsync por cópia

 Telemetry transversal: JSON logs, traces e metrics
 API: cloudorders-api | workers: cloudorders-*-worker
```

Os projetos da solution refletem esse desenho: `CloudOrders.Domain`, `CloudOrders.Application`, `CloudOrders.Infrastructure`, `CloudOrders.Api`, o worker de probe e os três business workers. Unit e integration tests verificam partes diferentes sem exigir toda a infraestrutura externa ao mesmo tempo.

## Um pedido bem-sucedido, do HTTP ao consumer

Considere um `POST /orders` com um `customerId` válido.

1. **HTTP e validação.** A API valida `customerId` e chama `CreateOrderHandler`. O endpoint retorna `201 Created` quando o caso de uso termina.
2. **Domínio.** O handler pede ao domínio uma `Order` com novo `Guid`, customer, timestamp fornecido por `TimeProvider` e status `Pending`. A entidade não conhece Azure, EF ou JSON.
3. **Application.** O handler constrói o contrato `OrderCreated`, mapeando `OrderStatus.Pending` para o status de integração `Pending`. Gera um `MessageId` novo, usa o `OrderId` como `CorrelationId` e fixa subject `CloudOrders.Orders.OrderCreated` e content type `application/json`.
4. **Transação local.** `IOrderStore.AddWithOutboxAsync` recebe Order, evento, metadata e trace context. `EfOrderStore` cria `OutboxMessage`, adiciona os dois ao `CloudOrdersDbContext`, executa `SaveChangesAsync` e faz commit explícito no PostgreSQL.
5. **Resposta.** Depois do commit, o handler retorna `CreatedOrder`; a API responde `201`. Nesse instante a Order e a intenção de publicação estão duráveis, mas o evento ainda pode estar pendente. A API não espera Azure Service Bus nem os workers.
6. **Polling.** O `OutboxDispatcherHostedService`, hospedado no processo da API, faz um batch inicial e depois usa `PeriodicTimer`; o default atual é batch 20 e polling de cinco segundos.
7. **Reconstrução do envelope.** O dispatcher busca linhas com `PublishedAtUtc == null`, por `CreatedAtUtc`, e reconstrói `PendingOutboxMessage` e `OutboundMessage`. O JSON é o payload de `OrderCreated`; MessageId e metadata nativo vêm de colunas próprias.
8. **Publicação.** `AzureServiceBusMessagePublisher` usa `ServiceBusMessageFactory` para mapear o envelope para `ServiceBusMessage` e envia ao topic `order-events`. O mesmo `MessageId` persistido é reutilizado em cada tentativa.
9. **Fan-out.** As subscriptions `payment`, `inventory` e `notification` possuem filtros para `CloudOrders.Orders.OrderCreated`. Cada uma recebe sua cópia independente; o probe usa outra subscription e outro subject.
10. **Processamento.** Cada wrapper cria `ServiceBusProcessor` com PeekLock, `AutoCompleteMessages = false` e `MaxConcurrentCalls = 1`, passando body, metadata, `DeliveryCount` e delegates de settlement ao processor independente do Azure SDK.
11. **Inbox.** O processor valida o contrato, desserializa `OrderCreated` e chama `IProcessedMessageStore.ExecuteOnceAsync` com sua identidade fixa: `Payment`, `Inventory` ou `Notification`. A claim e o negócio atual ficam coordenados no PostgreSQL.
12. **Simulação.** Para `Pending`, Payment retorna referência determinística de pagamento, Inventory uma reserva e Notification uma notificação no canal `simulated`. Não há providers reais.
13. **Settlement.** Depois do `Completed` do Inbox, cada processor chama `CompleteMessageAsync` somente para sua cópia. O mesmo pedido pode ser concluído em tempos diferentes nos três workers.
14. **Telemetria.** API, dispatcher e workers produzem logs estruturados, activities correlacionadas e metrics agregadas. `OrderId`, `MessageId`, `CorrelationId` e `TraceId` permitem navegar sem colocar o body no log.

A sequência pode ser visualizada assim:

```text
Cliente       API/Handler       PostgreSQL       Dispatcher       Service Bus       Workers
  |               |                |                |                |                |
  | POST /orders  |                |                |                |                |
  |-------------->|                |                |                |                |
  |               | BEGIN          |                |                |                |
  |               | Order + Outbox |                |                |                |
  |               |--------------->|                |                |                |
  |               | COMMIT         |                |                |                |
  |               |<---------------|                |                |                |
  |<--------------| 201 Created    |                |                |                |
  |               |                |                |                |                |
  |               |                | pending row    |                |                |
  |               |                |<---------------| poll           |                |
  |               |                |                | OutboundMessage |                |
  |               |                |                |--------------->| send           |
  |               |                |                |<---------------| accepted       |
  |               |                |                | mark published  |                |
  |               |                |                |--------------->|                |
  |               |                |                |                | fan-out        |
  |               |                |                |                |--------------->|
  |               |                |                |                | validate       |
  |               |                |                |                | Inbox + sim    |
  |               |                |                |                | commit         |
  |               |                |                |<---------------| complete       |
```

O diagrama é conceitual: cada worker tem sua própria cópia, seu próprio settlement e seu próprio registro de Inbox. A publicação e o processamento são assíncronos mesmo quando o smoke test observa tudo em poucos segundos.

## Garantias e não-garantias

### O que é garantido dentro da fronteira atual

- **Atomicidade local:** Order e `OutboxMessage` commitam ou sofrem rollback juntos no PostgreSQL.
- **Intenção durável:** se a API retorna sucesso, existe uma linha Outbox que o dispatcher poderá tentar; pending permanece elegível após falha de envio.
- **MessageId estável:** todas as tentativas de uma linha reutilizam o mesmo ID e o mesmo metadata de transporte; `AttemptCount` e `DeliveryCount` representam tentativas em fronteiras diferentes.
- **Processamento por consumer:** Inbox usa `(ConsumerName, MessageId)`, permitindo uma execução por consumer, não global do evento.
- **Settlement explícito:** workers completam somente após sucesso do processamento ou detecção de duplicate já concluído.
- **Retry limitado no broker:** nas subscriptions provisionadas conforme o README, falhas transitórias unsettled podem ser redelivered até `MaxDeliveryCount = 5`; falhas permanentes conhecidas vão à DLQ com razão estável.
- **Observação correlacionada:** logs, traces e metrics mostram identidades e outcomes sem expor payloads ou secrets.

### O que não é garantido

- **Não há atomicidade PostgreSQL + Service Bus.** O commit do banco e o aceite do broker são operações separadas.
- **Não há exactly-once publication.** Se o Service Bus aceitar e o update de `PublishedAtUtc` falhar, a linha será reenviada.
- **Não há exactly-once external effect.** O Inbox protege o processamento atual, mas não torna uma chamada futura de gateway atômica com PostgreSQL.
- **Não há request idempotency.** Repetir um POST é outra preocupação e está fora do escopo atual.
- **Não há múltiplos dispatchers coordenados.** O deployment inicial suporta uma única instância ativa do dispatcher.
- **Não há migrations automáticas.** A aplicação não chama `Database.Migrate()` no startup; migrations são uma ação explícita.
- **Não há providers de negócio reais.** Payment, Inventory e Notification são simulações determinísticas para `Pending`.

A palavra “garantia” precisa mencionar a fronteira: Outbox garante intenção no banco da Order, Inbox deduplicação no banco do consumer e o broker suas regras de delivery. Nenhum coordena todos os sistemas.

## Quatro falhas importantes

### 1. PostgreSQL falha durante a criação

Se a conexão falha, uma constraint é violada ou o commit não termina, `EfOrderStore` faz rollback e propaga a exceção. A API não retorna `201`: não há Order/Outbox confirmada nem chamada direta ao publisher.

O trace registra a falha e o span Npgsql/EF ajuda a separar conexão, comando ou commit. Logs usam tipo/outcome, não connection string ou parâmetros SQL. Sem request idempotency, repetir o request é uma nova tentativa de comando.

### 2. Service Bus está indisponível durante o dispatch

A API pode já ter respondido `201`, porque Order + Outbox commitou. Quando o dispatcher tenta enviar, `IMessagePublisher` falha. `EfOutboxMessageStore.RecordFailedAttemptAsync` incrementa `AttemptCount`, grava `LastAttemptAtUtc` e o tipo da exceção, mantendo `PublishedAtUtc` nulo. O próximo polling pode tentar novamente.

Apagar a linha perderia a intenção durável, e tornar a API síncrona ao Service Bus mudaria o contrato. Métrica `dispatch=failed`, trace com erro e log de falha dão a evidência operacional.

### 3. O envio funciona, mas marcar como publicado falha

Essa é a falha mais instrutiva:

```text
Service Bus aceita MessageId M
             |
             v
MarkPublishedAsync falha ou processo cai
             |
             v
PublishedAtUtc continua nulo
             |
             v
Próximo polling envia M novamente
             |
             v
Inbox do worker encontra (Consumer, M) Completed
             |
             v
Worker pula negócio e completa sua cópia
```

O Outbox não sabe se a resposta perdida significa “não enviado” ou “enviado”. Preserva a entrega e aceita duplicata possível. O `MessageId` estável liga a publicação ao Inbox: at-least-once publication com consumers idempotentes, não transação distribuída.

### 4. Consumer falha repetidamente

Se um worker encontra falha transitória no negócio ou PostgreSQL, não completa a mensagem e deixa a exceção propagar. No namespace provisionado conforme o README, o Service Bus redeliverá e, após cinco deliveries, a subscription usará a DLQ com `MaxDeliveryCountExceeded`.

A política separa falhas permanentes: subject, content type, body, JSON ou `MessageId` inválidos recebem `MessageContractViolation`; status não suportado recebe `UnsupportedBusinessStatus` após rollback do Inbox. Não faz sentido gastar cinco deliveries nesses casos.

Se o Inbox já commitou e só `CompleteMessageAsync` falhou, a redelivery não chama o negócio. Se o callback falhou antes do commit, o Inbox sofre rollback e a próxima tentativa pode executar de novo.

## Patterns used and the problem each solves

| Pattern/mecanismo | Uso no CloudOrders | Problema principal |
| --- | --- | --- |
| **Walking skeleton** | HTTP -> Application -> Domain -> PostgreSQL -> resposta | Validar a menor arquitetura executável antes de distribuir componentes |
| **Ports and adapters** | `IOrderStore`, `IMessagePublisher`, `IProcessedMessageStore` e implementações em Infrastructure | Manter regras e casos de uso independentes de EF/Azure |
| **Topic + subscriptions** | `order-events` com `payment`, `inventory` e `notification` | Fan-out e isolamento de consumidores |
| **Transactional Outbox** | Order e intenção `OrderCreated` no mesmo commit PostgreSQL | Evitar perder evento após commit da Order |
| **Inbox/idempotency** | `processed_messages` por `(ConsumerName, MessageId)` | Evitar efeito duplicado no consumer após redelivery |
| **Broker retry/DLQ** | Entrega unsettled, limite `MaxDeliveryCount = 5` configurado no broker, razões de DLQ | Recuperar falhas transitórias e separar poison messages |
| **OpenTelemetry** | Activities, logs JSON e metrics low-cardinality | Explicar o caminho, a taxa e a causa de falhas |
| **Migrations explícitas** | `InitialCreate`, `AddProcessedMessages`, `AddOutboxMessages` aplicadas fora do startup | Controlar evolução do schema e tornar mudanças revisáveis |

Request idempotency fica fora do escopo: resolve repetição do comando HTTP; Outbox resolve publicação e Inbox resolve consumo. São chaves e problemas diferentes.

## Por que a implementação foi construída nessa ordem

A ordem reduziu a incerteza:

1. **Walking skeleton.** API, Domain, Application, Infrastructure, Order mínima e PostgreSQL provaram a persistência antes de Azure.
2. **PostgreSQL e migrations.** Compose tornou o banco repetível; Testcontainers isolou integrações; migration explícita deixou o schema sob controle.
3. **Probe de Service Bus.** Topic, subscription, publisher port e completion foram testados antes do caso de uso.
4. **`OrderCreated` depois do transporte.** Contrato, metadata e status `Pending` foram definidos com uma rota conhecida; o fluxo direto expôs o dual write temporário.
5. **Workers independentes.** Payment, Inventory e Notification demonstraram fan-out sem gateways reais ou framework genérico.
6. **Inbox antes de providers.** Redelivery e settlement foram tratados com processadores determinísticos, tornando idempotência testável em PostgreSQL real.
7. **Retry/DLQ e observabilidade.** As fronteiras permitiram classificar falhas, limitar deliveries e seguir o fluxo sem mudar o contrato.
8. **Outbox por último.** Reutilizou `IMessagePublisher` e o Inbox para tornar duplicações de publicação seguras nos consumers.

Cada incremento deixou um seam verificável para o seguinte: persistência antes de evento, transporte antes de consumer, consumer antes de idempotência e idempotência antes de Outbox.

## What would change in a production-scale system

O desenho atual é uma base de estudo funcional, não uma topologia de produção completa. Em escala, seria preciso:

- **Dispatcher concorrente:** usar claims, leases, `FOR UPDATE SKIP LOCKED` ou equivalente antes de várias réplicas.
- **Retenção e replay:** definir arquivamento, reprocessamento de DLQ e proteção contra replay irreversível.
- **Providers reais:** adicionar timeout, autenticação, idempotency key, rate limit, reconciliação e estado próprio; a simulação não resolve efeitos parciais.
- **Escala de workers:** revisar `MaxConcurrentCalls`, lock renewal, throughput, ordenação e limites do provider.
- **Request idempotency:** aceitar uma chave de cliente para evitar duas Orders quando o mesmo comando HTTP for repetido.
- **Banco gerenciado:** alta disponibilidade, backup, restore testado, pooling, rotação de credenciais e política de migrations em pipeline.
- **Observabilidade central:** backend, alertas para Outbox pending, DLQ, redelivery, settlement e latência, com sampling e retenção.
- **Segurança e operação:** Managed Identity, RBAC, Key Vault, rede privada, health checks e runbooks.

## What deploy-to-azure will add, and what it should not redesign

O deploy-to-azure deverá adicionar Azure Service Bus Standard com topic/subscriptions/rules/DLQs, PostgreSQL gerenciado, compute, configuração segura, identidade/RBAC, rede, observabilidade e pipeline. Migrations devem continuar como etapa explícita e controlada, não efeito colateral do startup.

Também será necessário manter um único dispatcher até existir coordenação multi-instância, por réplica dedicada ou flag de ambiente; a escolha deve aparecer no deployment e runbooks.

O Azure não deve ser motivo para redesenhar o que já é uma boa fronteira:

- não transformar `POST /orders` em uma chamada síncrona a todos os providers;
- não colocar Order, evento e Service Bus na mesma “transação” sem protocolo distribuído real;
- não mover W3C trace context para o payload de negócio;
- não trocar o `MessageId` estável por um novo ID a cada retry;
- não eliminar Inbox porque o broker “parece confiável”;
- não ativar múltiplos dispatchers sem claims/leases;
- não executar migrations automaticamente em todos os workers;
- não criar um framework genérico antes de haver diferenças reais que justifiquem abstração;
- não descrever as simulações atuais como integrações reais.

## Mental model

Imagine uma loja distribuída. A API é o balcão que registra a venda; PostgreSQL é o livro oficial; o Outbox é a ficha de despacho presa ao mesmo registro da venda. Um funcionário leva fichas ao correio (Service Bus), que faz cópias para três departamentos. Cada departamento mantém seu próprio livro de recebimento (Inbox) e carimba sua cópia somente quando termina.

O balcão entrega o recibo antes de o correio concluir; o funcionário pode tentar a ficha novamente; um departamento pode parar sem parar os demais. Livros, carimbos, mensagens e rastros não formam transação universal: definem limites de durabilidade, repetição e investigação.

## Glossário

- **Distributed system:** processos que cooperam por rede e podem falhar independentemente.
- **Boundary:** fronteira de responsabilidade, dependência ou consistência.
- **Eventual consistency:** estado em que partes do sistema convergem depois, sem concluir simultaneamente.
- **Dual write:** necessidade de atualizar dois sistemas sem uma transação única entre eles.
- **Transactional Outbox:** gravação da mudança de negócio e da intenção de evento na mesma transação local.
- **Outbox row:** registro que o dispatcher usa para enviar o evento.
- **Dispatcher:** processo/host que publica Outbox após o commit do request.
- **Fan-out:** uma publicação distribuída para múltiplas subscriptions independentes.
- **Consumer:** processo que reage a uma cópia do evento.
- **Inbox:** estado durável do consumer usado para deduplicar processamento concluído.
- **Idempotency:** repetir uma operação sem produzir efeito adicional indesejado.
- **MessageId:** identidade da mensagem lógica; permanece igual em fan-out, redelivery e republicação do Outbox.
- **DeliveryCount / AttemptCount:** contador de entregas da cópia no broker / tentativas registradas de publicação no Outbox.
- **At-least-once:** entrega/publicação que pode ocorrer novamente e exige tratamento de duplicata.
- **Redelivery:** nova entrega pelo broker após falha ou ausência de settlement.
- **Settlement:** confirmação ou mudança do estado da mensagem no broker.
- **DLQ:** Dead Letter Queue, destino de mensagens retiradas do fluxo normal.
- **Poison message:** mensagem que falha repetidamente por contrato ou conteúdo não corrigível por retry.
- **CorrelationId:** identidade de correlação do evento com o negócio; no fluxo atual, o OrderId.
- **TraceId:** identidade técnica de uma trace.
- **Claim/lease:** posse temporária de trabalho, ainda não implementada para o dispatcher.
- **Request idempotency:** proteção específica contra repetição do mesmo comando HTTP.

## Perguntas de revisão

1. **O que acontece antes de a API retornar `201 Created`?**  
   O handler cria Order e `OrderCreated`, e `EfOrderStore` grava Order e `OutboxMessage` na mesma transação PostgreSQL. O Service Bus ainda não precisa ter aceitado o evento.

2. **Por que a publicação não é feita dentro de `AddWithOutboxAsync`?**  
   Porque essa operação controla uma transação PostgreSQL, e Azure Service Bus é outro resource manager sem participação nessa transação local. O dispatcher publica após o commit.

3. **Qual é a diferença entre pending e published no Outbox?**  
   Pending tem `PublishedAtUtc` nulo e será consultada pelo dispatcher; published recebeu timestamp após o envio e continua armazenada.

4. **Por que um retry do dispatcher reutiliza MessageId?**  
   Para que uma publicação repetida seja reconhecida pelos consumidores como a mesma entrega lógica e possa ser protegida pelo Inbox.

5. **O que o Inbox faz quando Payment, Inventory e Notification recebem o mesmo evento?**  
   Usa chaves independentes por `(ConsumerName, MessageId)`. Cada consumer pode processar uma vez; uma duplicata de Payment não suprime Inventory ou Notification.

6. **O que ocorre quando Service Bus está indisponível depois do `201`?**  
   O envio falha, a linha continua pending, a tentativa e o tipo da falha são registrados e o polling posterior tenta novamente.

7. **Por que o sistema ainda pode publicar duas vezes?**  
   O broker pode aceitar o envio antes que `MarkPublishedAsync` confirme `PublishedAtUtc`. A linha permanece pending e pode ser enviada novamente.

8. **Como uma falha transitória repetida termina?**  
   No namespace provisionado com `MaxDeliveryCount = 5` conforme o README, o worker não completa, o Service Bus redeliverá e a mensagem vai para a DLQ com `MaxDeliveryCountExceeded` após o limite.

9. **Por que o deploy não deve simplesmente ativar o dispatcher em todas as réplicas?**  
   O Outbox atual não possui claims, leases ou `FOR UPDATE SKIP LOCKED`; várias instâncias podem publicar a mesma linha concorrentemente. A implantação inicial suporta um dispatcher ativo.

10. **Por que uma trace completa não é uma transação distribuída?**  
    Trace relaciona operações tecnicamente; ela não faz commit coordenado. API, PostgreSQL, dispatcher, Service Bus e workers continuam com falhas e commits independentes.

## Cenários de entrevista

1. **“O cliente recebeu `201`, mas o pagamento ainda não ocorreu. A API está inconsistente?”**  
   Raciocínio sugerido: `201` confirma o commit local de Order + Outbox, não o processamento downstream. Verifique pending/dispatch e explique eventual consistency. Não acople o request a gateway apenas para esconder a natureza assíncrona.

2. **“Após uma indisponibilidade, a mesma mensagem aparece duas vezes no Payment Worker. Como provar que o efeito não duplicou?”**  
   Raciocínio sugerido: correlacionar MessageId, ConsumerName e estado `Completed` no Inbox. A primeira execução deve ter commitado antes do settlement; a segunda deve registrar duplicate e completar sem chamar o processador. Para provider real, exigir também idempotency key e reconciliação.

3. **“Duas réplicas da API publicam a mesma linha Outbox ao mesmo tempo.”**  
   Raciocínio sugerido: identificar ausência de claim/lease como causa. O Inbox pode proteger os três consumers atuais, mas não torna a topologia de dispatcher suportada. Manter um dispatcher ativo ou implementar coordenação PostgreSQL antes de escalar.

4. **“A equipe quer resolver uma falha de schema chamando `Database.Migrate()` em cada worker.”**  
   Raciocínio sugerido: preservar migrations explícitas e usar uma etapa controlada de deployment. Workers devem consumir o schema esperado; migration concorrente no startup mistura operação de aplicação com mudança de banco e dificulta rollback.

5. **“O dashboard mostra que a API está saudável, mas Notification tem mensagens pendentes.”**  
   Raciocínio sugerido: separar saúde HTTP de saúde downstream. Seguir a publicação, subscription e `cloudorders.messaging.process` de Notification; comparar redelivery, settlement, Inbox e métricas por consumer. Fan-out permite que Payment e Inventory estejam saudáveis enquanto Notification falha.

## Key takeaways

- CloudOrders é um sistema distribuído composto por API, PostgreSQL, dispatcher, Service Bus e três consumers.
- O pedido atravessa Domain e Application antes de chegar à persistência; Azure permanece em Infrastructure/adapters.
- Order e `OutboxMessage` são a unidade atômica local; o `201` não espera publicação.
- O dispatcher torna a intenção de evento durável e repete linhas pending com MessageId estável.
- `order-events` faz fan-out para Payment, Inventory e Notification, cada um com subscription e settlement próprios.
- O Inbox usa `(ConsumerName, MessageId)` para tornar redelivery segura no processamento atual.
- Retry/DLQ trata delivery e poison messages; não substitui idempotência nem reconciliação externa.
- OpenTelemetry conecta logs, traces e metrics sem colocar trace context no evento de negócio.
- A garantia é atomicidade local, publicação eventual e at-least-once com consumers idempotentes — não exactly-once universal.
- O deployment inicial deve manter um único dispatcher ativo e aplicar migrations explicitamente.
- Azure acrescentará recursos, identidade, rede e operação; não deve apagar as fronteiras já demonstradas.
