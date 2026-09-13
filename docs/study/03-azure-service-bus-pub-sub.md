# Azure Service Bus: pub/sub no CloudOrders

Mensageria introduz uma fronteira diferente da persistência. Em vez de a API chamar diretamente cada processamento, ela publica uma mensagem e consumidores independentes decidem o que fazer com ela. O CloudOrders adotou Azure Service Bus para estudar essa fronteira com um exemplo pequeno e observável.

Há uma distinção histórica importante. `add-service-bus-messaging` introduziu apenas um *messaging probe*: publisher, topic, subscription e worker temporário. `OrderCreated`, os workers de negócio, idempotência e retry/DLQ vieram depois; o Outbox veio ainda mais tarde. O probe explica a decisão original; a topologia atual mostra sua evolução.

## Queue versus Topic/Subscriptions

Uma queue representa, em geral, uma fila de trabalho. Vários consumidores podem competir pela mesma mensagem, mas cada mensagem é destinada a uma unidade lógica de processamento. Uma vez concluída, ela não continua disponível para os demais consumidores daquela fila.

Um topic representa publicação de eventos. As subscriptions são cópias lógicas independentes: cada uma recebe as mensagens que seu filtro aceita. Consumidores de subscriptions diferentes podem processar o mesmo evento para finalidades diferentes.

```text
QUEUE: trabalho compartilhado                 TOPIC: fan-out

Producer                                      Producer
    |                                             |
    v                                             v
  Queue                                      Topic: order-events
    |                                             |
    +--> Consumer A                              +--> Subscription A --> Consumer A
    +--> Consumer B                              +--> Subscription B --> Consumer B
    (competem pela mensagem)                     (cada subscription recebe sua cópia)
```

O CloudOrders não escolheu topic por ser mais sofisticado. O ADR 0004 registra a direção event-driven e a necessidade de consumidores de integration events independentes. Uma queue provaria entrega para um consumidor; `order-events` também permite adicionar Payment, Inventory e Notification sem um worker coordenar os demais.

## A escolha de `order-events`

O recurso central é o topic `order-events`. Na mudança original, sua subscription era `messaging-probe`. O objetivo não era publicar pedido: era provar o caminho `publisher → topic → subscription → consumer → completion` em Azure Service Bus Standard.

Hoje, o README documenta subscriptions adicionais no mesmo topic:

```text
CloudOrders.Api / Outbox Dispatcher (estado atual)
                    |
                    v
             Topic: order-events
                    |
                    +--> messaging-probe -- filter: Probe ---------> Messaging.Worker
                    +--> payment ----------- filter: OrderCreated -> Payment.Worker
                    +--> inventory --------- filter: OrderCreated -> Inventory.Worker
                    +--> notification ------ filter: OrderCreated -> Notification.Worker
```

O desenho permite que uma publicação de `OrderCreated` chegue independentemente aos três consumidores de negócio, enquanto o probe fica isolado. Isso é responsabilidade das regras de subscription, não de `if` espalhado no publisher.

Na versão original, a API publicava somente o probe e `POST /orders` continuava síncrono, persistindo e retornando `201 Created` sem evento. Mais tarde, `OrderCreated` foi introduzido e, no estado atual, a API grava o pedido e uma Outbox antes do dispatcher publicar. Essa última frase descreve a evolução atual; não descreve o escopo de `add-service-bus-messaging`.

## O que o Development messaging probe provou

`CloudOrders.Api/Program.cs` mapeia `POST /messaging/probe` somente em Development. O endpoint gera `messageId` e `correlationId`, cria `MessagingProbe` com uma mensagem e timestamp UTC, monta `MessageMetadata` e chama `IMessagePublisher`. Ele retorna `202 Accepted` somente depois que o publisher conclui o envio.

`IMessagePublisher` fica em `CloudOrders.Application/Messaging` e não expõe tipos Azure. `AzureServiceBusMessagePublisher`, em Infrastructure, usa `ServiceBusSender`; `ServiceBusMessageFactory` serializa o payload com `System.Text.Json` e cria a `ServiceBusMessage`. A API conhece uma porta; o adapter conhece o SDK.

`CloudOrders.Messaging.Worker` é um host separado. Seu `ServiceBusProbeWorker` cria um `ServiceBusProcessor` de longa duração para o topic e a subscription configurados. Ele valida subject e content type, desserializa o JSON em `MessagingProbe`, registra os dados e chama `CompleteMessageAsync` apenas depois do processamento bem-sucedido. A configuração original usou `AutoCompleteMessages = false` e `MaxConcurrentCalls = 1`.

Esse fluxo verificou credenciais, namespace, topic, subscription, filtro, serialização, processor e settlement. Os testes normais continuaram independentes de Azure: serialização é local e os testes PostgreSQL usam Testcontainers.

## Metadados nativos do Service Bus

O CloudOrders mantém os quatro campos em `MessageMetadata` e os copia para a mensagem nativa. O teste `MessagingSerializationTests` confirma que eles sobrevivem à criação da mensagem e que o JSON permanece desserializável.

| Campo nativo | Significado no CloudOrders | Exemplo |
| --- | --- | --- |
| `MessageId` | Identidade técnica da mensagem; deve permitir localizar uma entrega e, nas evoluções posteriores, sustentar deduplicação por consumidor. | Probe: GUID gerado; `OrderCreated`: identificador persistido no fluxo atual. |
| `CorrelationId` | Relação da mensagem com uma operação ou entidade correlacionada. | Probe: GUID de correlação; `OrderCreated`: ID do pedido no README/specs atuais. |
| `Subject` | Tipo lógico ou rota semântica da mensagem, usada também pelos filtros. | `CloudOrders.Messaging.Probe` ou `CloudOrders.Orders.OrderCreated`. |
| `ContentType` | Formato esperado do body. | `application/json`. |

Esses metadados são transport-level metadata. `MessageId` identifica a mensagem para o broker e o consumidor; não é necessário colocar `messageId` dentro de todo payload. `Subject` e `ContentType` já descrevem o envelope; duplicá-los no JSON cria dois valores que podem divergir. O probe contém apenas `Message` e `SentAtUtc`, sem repetir os quatro campos.

No evento atual, `OrderCreated` contém `OrderId` no JSON e usa o ID como `CorrelationId`. Isso é intencional: o primeiro é dado de negócio; o segundo permite correlação sem desserializar o body. Já o contexto W3C, na evolução com Outbox, fica fora do payload.

## Filtros SQL e a regra `$Default`

Uma subscription criada no Service Bus possui uma regra padrão que aceita todas as mensagens — a TrueFilter normalmente chamada `$Default`. Se o projeto cria uma regra específica sem remover a regra padrão, a específica não restringe o fluxo: a regra padrão continua aceitando tudo.

Por isso, o README primeiro remove `$Default` e depois cria a regra SQL. Para o probe:

```text
sys.Label = 'CloudOrders.Messaging.Probe'
```

Para as subscriptions atuais de negócio:

```text
sys.Label = 'CloudOrders.Orders.OrderCreated'
```

No SDK, `ServiceBusMessage.Subject` é o valor que aparece como label do sistema usado pela expressão. O filtro não examina o JSON; examina metadata nativa. O resultado é isolamento: a subscription `messaging-probe` não recebe `OrderCreated`, e as subscriptions de negócio não recebem o probe.

Remover `$Default` é parte da correção da topologia. Sem isso, um smoke test pode funcionar e ainda entregar mensagens erradas. Filtros tornam explícitos os eventos de cada subscription.

## PeekLock e completion explícito

Com PeekLock, o broker entrega a mensagem sob um lock temporário. O consumidor pode ler e processar sem removê-la definitivamente. Quando o trabalho termina, ele confirma o sucesso com `CompleteMessageAsync`. Se o processamento falha antes da completion, a mensagem não é confirmada e pode ser disponibilizada novamente segundo as regras do broker.

O worker do probe desabilita auto-completion para controlar a ordem: validar contrato, desserializar, registrar e somente então completar. Isso evita confirmar uma mensagem que não foi entendida. A mudança original não definiu uma política customizada de retry, abandon ou dead-letter; uma exceção simplesmente não deveria passar por completion. As políticas classificadas de retry/DLQ e os consumers idempotentes pertencem a mudanças posteriores documentadas nas specs canônicas.

PeekLock não transforma o processamento em exatamente uma vez. Pode haver redelivery por timeout, perda de lock, falha de processo ou falha de settlement. A evolução de idempotência do CloudOrders usa `MessageId` e identidade do consumidor em PostgreSQL para proteger os processadores atuais. Isso é uma extensão posterior para lidar com a realidade de entrega, não uma propriedade que o probe inicial prometia.

## Responsabilidades de producer e consumer

O producer decide quando há uma mensagem, escolhe o contrato e preenche metadata válida. No probe, `Api` gera a mensagem e chama `IMessagePublisher`; Infrastructure serializa e envia para o topic configurado. Ele não deve chamar Payment ou conhecer a implementação do worker.

O consumer escolhe sua subscription, valida o contrato que aceitou, desserializa, executa o processamento e faz settlement. `ServiceBusProbeWorker` não publica de volta nem acessa `CloudOrdersDbContext`. Esse limite reduz acoplamento e permite trocar o worker, testar seu processamento localmente e adicionar novas subscriptions sem reescrever o producer.

O código de composição usa `ServiceBusClient` e `ServiceBusSender` como singletons do host. `Messaging:TopicName` e `Messaging:SubscriptionName` têm defaults seguros; `ConnectionStrings:ServiceBus` deve vir de User Secrets ou environment variable, não do `.env` do PostgreSQL.

## Tier, garantias e custo

O README cria um namespace Azure Service Bus Standard porque Basic não suporta topics e subscriptions. Não foi uma preferência abstrata por um tier mais caro: o recurso arquitetural escolhido exige Standard. Para um projeto de estudo, a topologia ficou pequena — um namespace, um topic, uma subscription inicial, um worker e `MaxConcurrentCalls = 1` — e a criação dos recursos é manual, sem IaC ou deployment completo.

O projeto também evitou um framework genérico de messaging. Há uma porta concreta, um factory pequeno e um processor explícito. Isso reduz dependências e torna o experimento legível. A política SAS local documentada usa apenas Send e Listen e guarda a connection string em User Secrets; Managed Identity e RBAC são direção futura, não implementação desta mudança.

Service Bus confirma operações de transporte, mas não conhece se o efeito de negócio foi correto. Ele não garante que a API, o banco e o consumidor formem uma única transação; não elimina redelivery; e não fornece exactly-once para um gateway externo. O estado atual usa Outbox, inbox/idempotência e DLQ para lidar com falhas, mas essas garantias foram adicionadas por etapas. No início, o objetivo era provar transporte e completion explícito.

## Erros comuns e troubleshooting

- **`ConnectionStrings:ServiceBus` ausente:** `AddServiceBusClient` exige a configuração e falha claramente. Configure o User Secret de `CloudOrders.Api` ou `CloudOrders.Messaging.Worker`; não espere que `.env` do PostgreSQL seja lido.
- **Topic ou subscription com nome diferente:** confira `Messaging:TopicName` e `Messaging:SubscriptionName` e compare com `order-events` e `messaging-probe` no portal/CLI. O mesmo namespace não implica os mesmos nomes.
- **Namespace Basic:** topics/subscriptions não são suportados. O namespace de estudo precisa ser Standard.
- **`$Default` ainda ativo:** liste as rules da subscription. Remova a TrueFilter antes de criar `probe-only` ou `order-created-only`; caso contrário, o filtro específico não isola nada.
- **Subject incorreto:** `CloudOrders.Messaging.Probe` e `CloudOrders.Orders.OrderCreated` são contratos distintos. Um subject errado pode impedir o filtro ou fazer o worker rejeitar a mensagem.
- **Completion antes do processamento:** mantenha `AutoCompleteMessages = false` e complete somente depois de validar e desserializar. Logs de “completed” devem vir depois da chamada de completion.
- **Teste esperando Azure:** `MessagingSerializationTests` é local; as suites normais não precisam de namespace. O smoke test real é separado e exige recursos provisionados pelo desenvolvedor.

O smoke test documentado inicia API e worker, chama o endpoint Development, compara `messageId`, `correlationId` e subject nos logs e confirma que a subscription ficou sem mensagem ativa após completion. Assim, a topologia é testada sem cobrar Azure em cada execução automatizada.

## Mental model

Imagine o topic como um quadro de avisos e cada subscription como uma caixa de entrada especializada. O producer publica um envelope: o body carrega dados que o negócio entende; metadata nativa identifica, classifica e correlaciona a entrega. O filtro decide qual caixa recebe a mensagem. O consumer só considera o trabalho concluído quando termina o processamento e confirma explicitamente a entrega.

No CloudOrders, `order-events` é o ponto de distribuição; `messaging-probe` foi a primeira caixa; `payment`, `inventory` e `notification` são caixas posteriores. A arquitetura original provou a tubulação. As garantias mais fortes foram construídas depois, em camadas separadas.

## Glossário

- **Queue:** fila de trabalho em que consumidores competem por mensagens.
- **Topic:** destino de publicação que distribui mensagens para subscriptions.
- **Subscription:** cópia lógica filtrada de um topic e ponto de consumo independente.
- **Fan-out:** uma publicação que chega a vários consumidores por subscriptions distintas.
- **Producer/publisher:** componente que cria e envia mensagens.
- **Consumer:** componente que lê, processa e confirma mensagens.
- **PeekLock:** modo em que a mensagem fica bloqueada temporariamente antes de ser completada.
- **Settlement:** ação de confirmar ou rejeitar o resultado do processamento; o probe usa completion explícito.
- **TrueFilter / `$Default`:** regra padrão que aceita todas as mensagens.
- **Subject:** metadado nativo usado como tipo lógico e como base dos filtros do CloudOrders.
- **Delivery guarantee:** comportamento esperado em falhas; PeekLock permite redelivery, não exactly-once.

## Perguntas de revisão

1. **Qual é a diferença operacional entre queue e topic?**
   Queue distribui trabalho entre consumidores concorrentes; topic distribui cópias para subscriptions independentes.

2. **Por que `order-events` é melhor que uma queue para a direção futura do CloudOrders?**
   Porque permite que Payment, Inventory e Notification recebam cópias independentes sem o publisher conhecer cada consumidor.

3. **O que o probe validou antes de `OrderCreated` existir?**
   A fronteira de transporte: publicação, serialização, topic, filtro, leitura por worker, validação e completion explícito.

4. **Por que remover `$Default`?**
   Porque a TrueFilter aceita tudo; se permanecer, uma regra SQL específica não impede mensagens de outros tipos.

5. **PeekLock garante exatamente uma entrega?**
   Não. Ele permite completion explícito e redelivery quando a mensagem não é confirmada; tolerância a duplicatas é responsabilidade adicional do sistema.

## Cenários de entrevista

1. **“O worker recebe o probe, mas a subscription também contém `OrderCreated`. O filtro está errado?”**
   Raciocínio sugerido: primeiro verificar se `$Default` foi removido. Uma TrueFilter ativa aceita todos os subjects, mesmo com uma regra SQL adicional. Depois conferir `sys.Label` e `ServiceBusMessage.Subject`.

2. **“A equipe quer colocar `MessageId`, `Subject` e `ContentType` dentro de cada JSON para facilitar o debug. Você aceitaria?”**
   Raciocínio sugerido: manter esses valores como metadata nativa, onde broker e tooling já os entendem. Colocar cópias no body cria duplicidade e divergência; adicionar dados de negócio, como `OrderId`, é diferente porque o consumidor precisa deles para executar sua função.

3. **“Depois de processar uma mensagem, `CompleteMessageAsync` falha. O que pode ocorrer?”**
   Raciocínio sugerido: a entrega pode ser redeliverada; o transporte não deve ser confundido com o efeito de negócio. No CloudOrders atual, idempotência por consumer/`MessageId` foi adicionada posteriormente para tornar esse caso seguro.

## Key takeaways

- Queue é adequada para trabalho compartilhado; topic/subscriptions são adequados para fan-out.
- `order-events` foi escolhido para preservar consumidores independentes, não para aumentar complexidade sem motivo.
- O Development messaging probe validou a tubulação real antes dos eventos de negócio.
- `MessageId`, `CorrelationId`, `Subject` e `ContentType` formam o envelope nativo; não precisam ser repetidos indiscriminadamente no JSON.
- Filtros SQL só isolam subscriptions quando a TrueFilter `$Default` é removida.
- PeekLock com completion explícito controla o momento em que uma mensagem é confirmada, mas não fornece exactly-once.
- Standard foi necessário porque Basic não suporta topics e subscriptions.
- A mudança original foi pequena e econômica; Outbox, idempotência e retry/DLQ são evoluções posteriores claramente separadas.
