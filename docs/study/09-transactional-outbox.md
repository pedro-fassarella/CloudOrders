# Transactional Outbox no CloudOrders

O Transactional Outbox resolve uma pergunta específica: como garantir que uma mudança de negócio persistida no PostgreSQL também deixe registrada a intenção de publicar um evento, sem tentar colocar PostgreSQL e Azure Service Bus em uma transação distribuída improvisada?

No CloudOrders, a resposta é persistir `Order` e `OrderCreated` em `outbox_messages` na mesma transação local. Depois do commit, um dispatcher hospedado na API lê os registros pendentes e publica o evento. O request HTTP não espera o Service Bus; ele espera apenas o commit do banco. Isso cria uma arquitetura assíncrona, durável e explicitamente at-least-once.

Este capítulo descreve a implementação atual e o change arquivado `add-outbox-pattern`. O Outbox é posterior ao fluxo inicial de “persistir e publicar diretamente”. A publicação agora é mais resistente à queda entre banco e broker, mas ainda pode ocorrer mais de uma vez. O Inbox/idempotência dos workers torna essa duplicação segura para o processamento atual; não existe uma promessa universal de exactly-once.

## O dual write original

Antes do Outbox, o caminho conceitual era:

```text
POST /orders
     |
     v
COMMIT Order no PostgreSQL
     |
     v
Publish OrderCreated no Azure Service Bus
```

Esse código tem uma janela perigosa. Se o commit terminar e o processo cair antes de `PublishAsync`, o pedido existe, mas nenhum evento foi publicado. Se a aplicação responder ou avançar sem uma estratégia de recuperação, Payment, Inventory e Notification nunca saberão daquela Order.

O inverso também é problemático: publicar primeiro e falhar ao persistir deixa um evento apontando para uma Order que não existe. Escolher uma ordem reduz um tipo de inconsistência, mas não elimina o dual write. São dois sistemas com protocolos diferentes.

## Por que uma transação normal não inclui o Service Bus

`EfOrderStore` usa uma transação PostgreSQL através do `CloudOrdersDbContext`. Essa transação controla inserts e updates no mesmo banco, mas não transforma `ServiceBusSender.SendMessageAsync` em uma operação participante dela. PostgreSQL e Azure Service Bus são resource managers separados, com conexões, commits e falhas independentes.

O CloudOrders não implementa uma distributed transaction coordinator, não mantém uma transação aberta durante a chamada Azure e não possui um protocolo de commit de duas fases entre esses serviços. Mesmo que alguém tentasse manter a transação PostgreSQL aberta até o envio, ainda existiriam timeouts, quedas e decisões de recuperação sem uma garantia comum.

O Outbox aceita essa fronteira em vez de escondê-la: a transação local garante a existência durável da intenção de publicar; o dispatcher tenta o lado externo depois. Não é atomicidade entre banco e broker. É atomicidade entre o estado de negócio e o comando persistido que permitirá a publicação.

## A transação exata do CloudOrders

`CreateOrderHandler` cria a entidade de domínio, constrói o contrato `OrderCreated` e gera `MessageMetadata`. Ele não recebe um publisher para enviar diretamente. A chamada é a porta especializada:

```text
IOrderStore.AddWithOutboxAsync(
    order,
    orderCreated,
    metadata,
    traceContext)
```

Em `EfOrderStore`, o fluxo é:

```text
BEGIN PostgreSQL
  dbContext.Orders.Add(order)
  dbContext.OutboxMessages.Add(outboxMessage)
  SaveChangesAsync()
COMMIT
```

Se qualquer insert ou o commit falhar, o código faz rollback e propaga a exceção. Order e Outbox não ficam parcialmente gravados. Não foi criado um Unit of Work genérico: `AddWithOutboxAsync` expressa a operação concreta que o caso de uso precisa.

```text
                    mesma transação
POST /orders ------------------------------+
     |                                     |
     v                                     v
  Order criada                    OutboxMessage pendente
     |                                     |
     +--------------- COMMIT -------------+
                       |
                       v
              API retorna 201 Created
              sem esperar o Service Bus
```

O `201 Created` indica que o pedido e seu registro Outbox foram commitados. Não indica que `order-events` já recebeu a mensagem nem que os três workers terminaram. A consistência downstream é eventual.

## O que `outbox_messages` guarda

O modelo EF `OutboxMessage` e a migration `20260912213142_AddOutboxMessages` definem a memória durável do dispatcher. A tabela tem uma chave primária técnica `Id` e índice único para `message_id`, além de índices para ordem e consulta de pendentes.

Os campos importantes são:

- `order_id`: vínculo com o negócio de origem;
- `message_id`: identidade nativa estável do evento;
- `correlation_id`: no `OrderCreated`, o ID da Order;
- `subject`: `CloudOrders.Orders.OrderCreated`;
- `content_type`: `application/json`;
- `payload_json`: bytes lógicos serializados do contrato;
- `created_at_utc`: ordem de criação e polling;
- `published_at_utc`: nulo enquanto pendente; o estado é atualizado após envio reconhecido, embora o valor gravado seja o horário capturado no início da tentativa;
- `attempt_count`: número de tentativas que puderam ser registradas;
- `last_attempt_at_utc`: momento da última tentativa;
- `last_error_type`: tipo da última falha, sem armazenar exception message;
- `trace_parent` e `trace_state`: contexto W3C técnico opcional.

O payload não é um saco de metadata. `payload_json` contém somente os dados de `OrderCreated`: `orderId`, `customerId`, `status` e `createdAtUtc`. Subject, content type, IDs técnicos e trace context ficam em colunas próprias. Essa separação permite ao publisher reconstruir o envelope sem poluir o contrato de negócio.

## MessageId estável e envelope de transporte

Durante `CreateOrderHandler`, o `MessageId` do envelope lógico é gerado uma vez com um GUID em formato string. O valor é salvo no `OutboxMessage`; cada leitura posterior o transforma em um `OutboundMessage` com `MessageId`, `CorrelationId`, `Subject`, `ContentType` e bytes do payload. `AttemptCount` acompanha tentativas registradas de publicação; ele não é o `DeliveryCount` de uma cópia recebida por um worker.

`AzureServiceBusMessagePublisher` passa o `OutboundMessage` para `ServiceBusMessageFactory`, que mapeia esses campos para `ServiceBusMessage`. Em uma nova tentativa, o dispatcher não cria outro GUID. Ele reutiliza a identidade persistida e o metadata nativo.

Isso é essencial para o Inbox. Se um envio confirmado for seguido por uma falha ao marcar `PublishedAtUtc`, o mesmo evento pode ser reenviado. Payment, Inventory e Notification receberão o mesmo `MessageId` e poderão consultar suas chaves `(ConsumerName, MessageId)`. Um novo ID a cada polling pareceria uma nova mensagem para os consumidores e inutilizaria a deduplicação existente.

O caminho tipado `PublishAsync<TPayload>` continua disponível para o endpoint Development `/messaging/probe`. O dispatcher usa a sobrecarga de `OutboundMessage`, preservando exatamente o envelope persistido. Assim, a mudança do Outbox não alterou o contrato do probe nem de `OrderCreated`.

## Dispatcher hospedado e polling

`OutboxDispatcherHostedService` é um `BackgroundService` registrado pela API quando `Outbox:Enabled` é verdadeiro. `OutboxDispatcherOptions` lê três valores, com defaults atuais de enabled, batch size 20 e intervalo de cinco segundos, e rejeita inteiros não positivos.

O ciclo de vida é:

```text
API inicia
   |
   v
DispatchBatchAsync imediato
   |
   v
PeriodicTimer(Outbox:PollingIntervalSeconds)
   |
   +--> a cada tick: lê pendentes mais antigos até BatchSize
                         |
                         v
                    publica um por vez
                         |
                         v
                    atualiza pending count
```

Em cada batch, o hosted service cria um escopo, resolve `IOutboxMessageStore`, chama `GetPendingAsync`, ordena por `CreatedAtUtc` e processa sequencialmente. `EfOutboxMessageStore` seleciona somente linhas com `PublishedAtUtc == null`, limita pelo batch size e reconstrói `PendingOutboxMessage`/`OutboundMessage`.

O dispatcher chama `IMessagePublisher.PublishAsync(outboundMessage)`. Se o envio retorna sucesso, chama `MarkPublishedAsync`. Esse update preenche `PublishedAtUtc`, atualiza `LastAttemptAtUtc`, incrementa `AttemptCount` e limpa `LastErrorType`. A mudança de estado acontece depois do sucesso, mas o timestamp usado foi capturado antes do envio; ele representa o início da tentativa, não o instante exato de confirmação do broker. Registros publicados não são apagados.

Se o envio falha, o dispatcher chama `RecordFailedAttemptAsync`: incrementa a contagem, registra o momento e salva apenas o tipo da exceção. `PublishedAtUtc` continua nulo, portanto a linha volta ao próximo polling. Se o envio funcionou, mas `MarkPublishedAsync` falhou, a linha também permanece pendente. Esse caso é deliberado: o sistema prefere tentar de novo a correr o risco de perder o evento.

```text
GetPendingAsync
      |
      v
OutboundMessage persistido
      |
      v
IMessagePublisher -> ServiceBusSender
      |
  +---+-------------------+
  |                       |
  v                       v
sucesso                  falha
  |                       |
  v                       v
MarkPublished        RecordFailedAttempt
PublishedAt != null  PublishedAt continua null
```

O polling não usa uma fila local nem depende de o request ainda estar vivo. Enquanto a linha estiver pendente e o dispatcher estiver habilitado, existe uma oportunidade posterior de publicação.

## A janela de crash após o envio

O Outbox remove a janela “Order commitou, mas a intenção de publicar sumiu”. Ele não remove a janela entre o envio no broker e o update da linha:

```text
Dispatcher lê linha pendente
        |
        v
Service Bus aceita OrderCreated
        |
        v
Processo cai ou MarkPublishedAsync falha
        |
        v
PublishedAtUtc continua NULL
        |
        v
Próximo polling envia o mesmo MessageId
        |
        v
Worker recebe redelivery/duplicata
        |
        v
Inbox encontra (ConsumerName, MessageId) Completed
        |
        v
Worker pula o negócio e completa a entrega
```

Essa é a razão para chamar a solução de at-least-once publishing. O dispatcher garante que uma linha pendente será tentada, mas não consegue saber com certeza se uma chamada cujo resultado se perdeu foi aceita pelo Service Bus. Reusar o `MessageId` torna a ambiguidade tratável pelos consumidores atuais.

O Inbox é uma proteção do lado consumidor, não uma confirmação retroativa do Outbox. Se um novo consumer não for idempotente, a publicação duplicada poderá repetir seu efeito. Se o efeito for uma chamada externa, a chave técnica só protege a operação se o provider aceitar uma idempotency key ou se existir uma estratégia de reconciliação.

## Outbox, Inbox, broker e request idempotency

Esses mecanismos são complementares:

| Mecanismo | Fronteira que protege | Problema resolvido | O que não resolve |
| --- | --- | --- | --- |
| **Outbox** | Produtor + banco | Não perder a intenção de publicar junto com Order | Envio duplicado ou exatamente uma entrega |
| **Inbox/idempotência** | Consumer + banco | Não repetir negócio após redelivery do mesmo consumer | Publicação, efeitos externos arbitrários ou request duplicado |
| **Retry/DLQ do broker** | Entrega Service Bus | Redeliver falhas transitórias e limitar poison messages | Saber se o negócio externo já ocorreu |
| **Request idempotency** | Cliente + API | Repetição do mesmo comando HTTP | Redelivery downstream e falha entre sistemas |

Request idempotency continua fora do escopo atual. O Outbox pode registrar dois eventos se dois requests criarem duas Orders; ele não decide se dois POSTs do cliente representam uma única intenção. Essa é outra chave, outra política e outra fronteira.

## W3C, traces, logs e métricas do Outbox

O `CreateOrderHandler` cria `cloudorders.order.create`, captura o contexto W3C e cria `cloudorders.outbox.persist`. O `OutboxTraceContext` guarda `trace_parent` e `trace_state` na tabela, sem colocá-los no JSON de `OrderCreated`.

Quando o dispatcher encontra contexto válido, `CloudOrdersTelemetry.StartOutboxDispatch` inicia `cloudorders.outbox.dispatch` com esse contexto como parent e `ActivityKind.Producer`. Isso permite relacionar a persistência original com o envio atrasado. Os logs incluem `OrderId`, `MessageId` e `CorrelationId`, sem payload, secrets ou exception text.

O meter registra `cloudorders.outbox.persisted`, `cloudorders.orders.published`, `cloudorders.outbox.dispatch.attempts` e o gauge `cloudorders.outbox.pending`. As tentativas usam somente outcome controlado, como `published` ou `failed`; IDs não viram metric dimensions. Uma métrica informa a saúde agregada do dispatcher, e a trace explica uma tentativa individual.

## Limitação de um dispatcher ativo

O deployment inicial exige exatamente uma instância ativa do Outbox Dispatcher. Se várias réplicas da API rodarem com `Outbox:Enabled=true`, elas podem consultar a mesma linha pendente e publicar concorrentemente. O `MessageId` estável e o Inbox tornam a duplicata de negócio atual mais segura, mas não transformam a concorrência do dispatcher em uma topologia suportada.

Por isso, ao escalar a API, uma réplica deve manter o dispatcher ativo e as demais devem usar `Outbox:Enabled=false`. O código não usa claim PostgreSQL, lease renovável, `FOR UPDATE SKIP LOCKED` ou row-lock coordination. Todas essas opções poderiam coordenar donos temporários de uma linha, mas introduziriam decisões de lease, expiração, recuperação e concorrência que foram deliberadamente adiadas.

## Cleanup, retenção e poison Outbox

Linhas publicadas são mantidas. A implementação atual não oferece cleanup/retention, replay administrativo, limite de tentativas, backoff sofisticado nem tratamento específico para uma linha que falha em todo polling. Uma falha permanente no publisher pode deixar uma linha pendente e aumentar `AttemptCount` indefinidamente enquanto o dispatcher continuar ativo.

Isso não é a mesma coisa que a DLQ do Service Bus. Uma poison outbox row falha antes ou durante a publicação; ela ainda não é uma mensagem entregue ao broker. Para o futuro, seria necessário definir classificação, alerta, pausa, correção, replay seguro, retenção de linhas publicadas e auditoria. Esses mecanismos ficaram fora do change arquivado.

## O que os testes provam

Os testes unitários de `OutboxDispatcherHostedService` verificam que o caminho feliz publica o envelope e só depois marca a linha como publicada; que uma falha mantém a linha pendente; que retry reutiliza `MessageId` e metadata nativo; e que uma falha em `MarkPublishedAsync` permite uma segunda publicação segura com o mesmo ID. Também há cobertura do parent W3C persistido e da validação de `OutboxDispatcherOptions`.

Os testes de `CreateOrderHandler` verificam a criação do `OrderCreated`, metadata estável, trace context e a ausência de sucesso quando a persistência atômica falha. A integração PostgreSQL/Testcontainers cobre commit e rollback de Order + Outbox, sem depender de um namespace Azure. A validação arquivada registrou build sem warnings, 54 unit tests, 11 integration tests e nenhum model change pendente.

Esses testes não provam exactly-once no Service Bus. Eles provam a propriedade que o código controla: intenção durável, envelope preservado, retry de linha pendente e comportamento conhecido quando o envio já ocorreu, mas a confirmação no banco não ocorreu.

## Mental model

Imagine uma loja com um livro-caixa e uma caixa de despachos. Ao vender, a loja escreve a venda e uma ficha de envio no mesmo livro, e só então entrega o recibo ao cliente. Um funcionário separado lê as fichas e leva os pacotes ao correio.

Se o funcionário não sabe se o correio recebeu o pacote, ele tenta novamente com o mesmo identificador. O destinatário consulta seu próprio registro para não executar a entrega duas vezes. O livro não garante que o correio recebeu exatamente uma vez; garante que a intenção de envio não desapareceu junto com a venda.

No CloudOrders: o livro é PostgreSQL, a ficha é `OutboxMessage`, o funcionário é o dispatcher, o correio é o Service Bus e o registro do destinatário é o Inbox de cada worker.

## Glossário

- **Dual write:** atualização de dois sistemas que não compartilham uma transação única.
- **Transactional Outbox:** padrão que grava mudança de negócio e evento pendente na mesma transação local.
- **OutboxMessage:** registro persistente do evento e de seu envelope de transporte.
- **Pending:** linha com `PublishedAtUtc` nulo e elegível para polling.
- **Published:** linha com `PublishedAtUtc` preenchido; permanece armazenada.
- **Dispatcher:** hosted service que lê Outbox pendente e publica depois do commit.
- **OutboundMessage:** envelope provider-neutral com IDs, metadata e bytes do payload.
- **Stable MessageId:** identidade gerada uma vez e reutilizada em todas as tentativas.
- **Inbox:** registro do consumer que evita repetir processamento concluído.
- **At-least-once publishing:** política em que a intenção é tentada, mas a publicação pode duplicar.
- **Poison outbox row:** linha que falha repetidamente antes de ser marcada como publicada.
- **Lease/claim:** mecanismo futuro para atribuir temporariamente uma linha a um dispatcher.
- **W3C trace context:** metadata técnica de parent/trace propagada fora do payload de negócio.

## Perguntas de revisão

1. **Qual problema o Outbox resolve?**  
   Ele grava Order e a intenção de publicar `OrderCreated` na mesma transação PostgreSQL, evitando que o evento desapareça após um commit de negócio.

2. **Por que a API retorna `201 Created` antes do Service Bus?**  
   Porque o contrato do request termina no commit de Order + Outbox. A publicação é eventual e pertence ao dispatcher assíncrono.

3. **O que acontece se o envio funciona, mas `MarkPublishedAsync` falha?**  
   A linha permanece pendente e pode ser enviada novamente com o mesmo `MessageId`. O Inbox dos consumidores atuais pula o negócio já concluído.

4. **Por que não gerar um novo MessageId em cada retry?**  
   Porque isso faria a redelivery parecer uma nova mensagem e impediria o Inbox `(ConsumerName, MessageId)` de reconhecer a duplicata.

5. **Por que apenas um dispatcher ativo é suportado?**  
   Porque o polling não possui claims, leases ou `FOR UPDATE SKIP LOCKED`; várias instâncias podem publicar a mesma linha concorrentemente.

## Cenários de entrevista

1. **“A Order foi criada, mas o Service Bus ficou indisponível por dez minutos. O que o sistema deve mostrar?”**  
   Raciocínio sugerido: o POST pode retornar `201` se Order e Outbox commitarem. A linha permanece pending, `AttemptCount`/`LastErrorType` registram as tentativas, e o dispatcher tenta novamente sem exigir que o cliente repita o POST.

2. **“O publisher confirma o envio, mas a API cai antes de marcar `PublishedAtUtc`. Isso quebra a consistência?”**  
   Raciocínio sugerido: produz uma publicação duplicada possível, não perda silenciosa da intenção. O retry reutiliza MessageId; consumers idempotentes tratam a segunda entrega como duplicata. A garantia continua at-least-once.

3. **“A equipe quer ativar o dispatcher em todas as réplicas da API e confiar apenas no Inbox.”**  
   Raciocínio sugerido: o Inbox protege os consumers atuais, mas não coordena donos do Outbox nem protege todos os futuros efeitos. Manter uma única instância ativa ou implementar claims/leases antes de escalar o dispatcher.

## Key takeaways

- O problema original era um dual write entre commit PostgreSQL e publish Service Bus.
- O Outbox torna Order + intenção de evento atômicos no PostgreSQL, não banco + broker.
- A API retorna `201` após o commit e deixa a publicação para o dispatcher assíncrono.
- `outbox_messages` preserva payload `OrderCreated`, metadata nativa, MessageId estável, tentativas e trace context técnico.
- Linhas pending continuam elegíveis; falhas de envio não são apagadas nem marcadas como publicadas.
- O mesmo MessageId é reutilizado para que o Inbox possa proteger redelivery.
- O crash após envio e antes de `PublishedAtUtc` ainda permite publicação duplicada.
- Inbox, retry/DLQ do broker e request idempotency resolvem fronteiras diferentes.
- O dispatcher atual é single-active; claims, leases, `SKIP LOCKED`, cleanup, retenção e replay ficaram adiados.
- A garantia real é at-least-once publishing com consumers idempotentes, não distributed exactly-once.
