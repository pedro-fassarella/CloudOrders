# `OrderCreated` e o fluxo event-driven do CloudOrders

Um evento de negócio registra um fato relevante para o negócio: algo aconteceu e outros componentes podem reagir. No CloudOrders, a criação de um pedido é o fato que interessa. O pedido nasce no domínio, é persistido e, depois, pode ser anunciado para consumidores como Payment, Inventory e Notification.

Há uma precisão importante de linguagem. `OrderCreated` não é uma entidade do domínio nem uma cópia serializada de `Order`; no código ele é um *integration event* definido em `CloudOrders.Application.Messaging`. Ele representa o fato de negócio em uma forma estável para atravessar processos. A regra interna de `Order` continua no Domain; o contrato externo tem apenas os dados que consumidores precisam.

## O contrato `OrderCreated`

O record atual é:

```csharp
public sealed record OrderCreated(
    Guid OrderId,
    string CustomerId,
    string Status,
    DateTimeOffset CreatedAtUtc)
```

O JSON esperado contém `orderId`, `customerId`, `status` e `createdAtUtc`. Esses campos descrevem o pedido criado, não o transporte que o carrega. O contrato não inclui EF entity, objeto `Order`, `MessageId`, `CorrelationId`, subject, content type, credenciais ou detalhes de infraestrutura.

Essa separação tem três efeitos. Primeiro, evita acoplar consumidores ao modelo de persistência. Segundo, permite que o wire contract evolua com intenção, em vez de mudar quando uma propriedade privada do domínio ou um mapping do EF mudar. Terceiro, deixa o body útil para qualquer consumidor que conheça o evento, mesmo que ele não use todas as informações de transporte.

`OrderCreated` vive em `Application/Messaging` porque a mudança arquivada ainda tinha uma única integração real. O ADR e o design não criaram um projeto `Contracts`. A decisão foi evitar um assembly adicional antes de existir necessidade concreta de uma fronteira de contratos independente.

## Por que o status é mapeado explicitamente

No domínio atual, `OrderStatus` possui `Pending`. O contrato de integração, porém, usa uma string estável: `OrderCreated.PendingStatus`, cujo valor é `Pending`. `CreateOrderHandler` chama `MapStatus` e faz um `switch` explícito:

```text
OrderStatus.Pending  ->  OrderCreated.PendingStatus  ->  "Pending"
```

O handler não usa cegamente `OrderStatus.ToString()`. Isso é uma proteção contra acoplamento acidental entre o nome de um enum interno e o contrato na rede. Um rename interno, uma mudança de nomenclatura ou a introdução de um estado que ainda não tenha semântica para consumidores não deve alterar mensagens silenciosamente.

O mapping também cria um ponto de falha visível: um valor de domínio desconhecido causa erro até que alguém decida conscientemente qual será seu valor wire. Essa pequena cerimônia é importante porque contratos de integração duram mais que uma implementação local. Os testes verificam que um pedido `Pending` produz exatamente `Pending` no evento.

## Os quatro metadados nativos

O evento tem dados de negócio e um envelope de transporte. `MessageMetadata` na Application reúne os campos que `ServiceBusMessageFactory` copia para `ServiceBusMessage`:

| Metadado | Papel no evento | Valor de `OrderCreated` |
| --- | --- | --- |
| `MessageId` | Identidade técnica da mensagem lógica/envelope. | No fluxo original, um GUID novo; no estado atual, valor persistido e reutilizado em republicações seguras. |
| `CorrelationId` | Identidade usada para relacionar a mensagem a uma operação ou entidade. | `OrderId` em formato `D`. |
| `Subject` | Nome lógico do evento e base do filtro do topic. | `CloudOrders.Orders.OrderCreated`. |
| `ContentType` | Formato esperado do body. | `application/json`. |

`OrderId` é a identidade de correlação de negócio porque é o identificador que todas as reações ao pedido devem conseguir relacionar. O `CorrelationId` nativo permite rastrear essa relação sem desserializar o JSON. O `MessageId` identifica o envelope lógico, não cada tentativa: `DeliveryCount` identifica redeliveries do broker e `AttemptCount` registra tentativas persistidas do Outbox. Uma mesma Order pode ter várias tentativas sem deixar de ser o mesmo fato de negócio.

Também não há motivo para repetir metadata de transporte dentro de cada body. Inserir `MessageId`, `Subject` ou `ContentType` no JSON criaria duas fontes de verdade que poderiam divergir. O body precisa de `OrderId` porque esse é dado de negócio; não precisa de uma segunda cópia de `Subject`. Os testes de serialização confirmam tanto o JSON quanto os quatro metadados nativos sem conexão de rede.

## O fluxo original: persistir e depois publicar

A mudança arquivada `add-order-created-event` conectou o caso de uso existente ao publisher. A sequência era:

```text
Cliente
  |
  | POST /orders
  v
CloudOrders.Api
  |
  v
CreateOrderHandler
  |
  +--> Order.Create(...)                    [Domain]
  |
  +--> IOrderStore.AddAsync(order)          [Application port]
  |          |
  |          +--> EfOrderStore / DbContext --> PostgreSQL commit
  |
  +--> cria OrderCreated + MessageMetadata
  |
  +--> IMessagePublisher.PublishAsync(...)
             |
             +--> ServiceBusMessageFactory
             +--> AzureServiceBusMessagePublisher
             +--> topic order-events
  |
  +--> HTTP 201 Created
```

O handler só publicava depois que `IOrderStore.AddAsync` concluía. Se a persistência falhasse, nenhum publish era tentado. Se o publish falhasse depois do commit, a exceção subia até a requisição e o pedido permanecia gravado; a API não conseguia desfazer automaticamente aquela gravação. O evento não era uma transação com o banco.

## O problema de dual-write

Esse fluxo escreve em dois sistemas: PostgreSQL e Service Bus. Mesmo com `await` em ambas as operações, não existe uma transação distribuída entre elas. Há duas janelas principais:

1. PostgreSQL confirma e Service Bus falha: existe pedido sem evento.
2. Service Bus aceita a mensagem, mas a resposta ou o processo falha antes de concluir o fluxo: o pedido pode estar criado e o cliente pode não ter recebido uma resposta normal; uma repetição pode criar outro pedido ou outro evento.

Na etapa original, essa limitação foi documentada como temporária. O objetivo era introduzir o evento real e provar a integração com o publisher existente, não resolver confiabilidade de publicação. Não havia Outbox, retry, idempotência ou recovery nessa mudança.

O teste de integração do change substituía `IMessagePublisher` por um capturador em memória. Assim, podia chamar a API com PostgreSQL Testcontainers e verificar o evento sem namespace Azure. Os testes também cobriam a ordem: persistência antes de publicação, falha de persistência sem tentativa de publish e falha do publisher observável pela requisição. No estado atual, os testes foram adaptados para verificar a Outbox e a ausência de publicação direta.

## Eventual consistency e consumidores downstream

Publicar `OrderCreated` não significa que Payment, Inventory ou Notification terminou. Significa que o fato foi colocado na fronteira de mensageria; os consumidores podem receber a mensagem em outro processo, em outro momento e com outra velocidade.

Essa é a essência da eventual consistency. A resposta HTTP confirma o compromisso da etapa que o contrato define, não todos os efeitos derivados. No fluxo direto original, `201 Created` só ocorria depois da persistência e do retorno bem-sucedido do publisher, mas os workers downstream ainda eram assíncronos. O request não esperava o pagamento simulado, a reserva ou a notificação.

Manter consumidores fora da transação HTTP evita que a API fique acoplada ao tempo de execução e à disponibilidade de cada reação. Payment pode falhar ou ficar lento sem bloquear a criação de todo pedido; cada subscription pode aplicar seu próprio processamento. O custo é aceitar estados intermediários e projetar redelivery, duplicatas e observabilidade de forma explícita.

## Preparação para fan-out

Mesmo sem workers de negócio na mudança original, o contrato foi desenhado para fan-out. O subject `CloudOrders.Orders.OrderCreated` é estável; o topic é `order-events`; e subscriptions futuras podem filtrar esse subject independentemente. A API publica o fato, não chama três serviços.

No estado atual, o README documenta `payment`, `inventory` e `notification` como subscriptions independentes. Cada uma recebe uma cópia filtrada de `OrderCreated` e executa seu próprio slice. O antigo `messaging-probe` continua filtrado para `CloudOrders.Messaging.Probe`, preservando seu papel de diagnóstico. A mudança original preparou essa topologia sem antecipar processadores reais, bancos de Payment ou provedores externos.

## What changed later with Outbox

A evolução posterior com `add-outbox-pattern` mudou o mecanismo de publicação, mas preservou o contrato `OrderCreated`, o topic e os metadados de transporte. Hoje `CreateOrderHandler` grava Order e `OutboxMessage` na mesma transação; o dispatcher publica o envelope pendente depois do commit e reutiliza o mesmo `MessageId` se uma marcação posterior falhar.

Assim, a Outbox torna durável a intenção de publicar, mas não fornece exactly-once sozinha. Os detalhes de polling, tentativas, timestamps e limitação de dispatcher pertencem ao [capítulo 09](./09-transactional-outbox.md); Inbox e retry/DLQ são tratados nos capítulos [06](./06-idempotent-consumers-and-inbox.md) e [07](./07-retry-and-dead-letter.md).

## Mental model

Pense em `OrderCreated` como um cartão que diz: “o pedido X foi criado com estes dados”. O cartão tem duas partes:

1. **Fato de negócio:** `OrderId`, `CustomerId`, `Pending` e `CreatedAtUtc`.
2. **Envelope de transporte:** `MessageId`, `CorrelationId`, `Subject` e `ContentType`.

O producer cria o cartão depois de conhecer o pedido; o topic distribui cópias; cada consumer reage em seu próprio tempo. A Outbox posterior mudou onde o cartão espera antes de ser enviado, não o fato escrito nele.

## Glossário

- **Business event:** representação de um fato relevante que já aconteceu no domínio.
- **Integration event:** versão estável do fato preparada para cruzar fronteiras de processo.
- **Wire contract:** formato serializado que consumidores recebem.
- **Producer/publisher:** componente que cria e envia a mensagem.
- **Consumer:** componente que recebe e executa uma reação.
- **CorrelationId:** valor técnico para relacionar mensagens a uma operação ou entidade.
- **MessageId:** identidade técnica de uma mensagem específica.
- **Dual-write:** escrita em dois sistemas sem uma transação única entre eles.
- **Eventual consistency:** convergência posterior entre o fato criado e seus efeitos derivados.
- **Transactional Outbox:** persistência atômica do fato e da intenção de publicação, seguida de envio assíncrono.

## Perguntas de revisão

1. **`OrderCreated` é a entidade `Order` serializada?**
   Não. É um integration event com quatro valores escalares de negócio, sem EF, detalhes internos ou metadata de transporte.

2. **Por que mapear `OrderStatus.Pending` para uma constante?**
   Para manter o contrato wire estável e falhar explicitamente quando surgir um status sem mapeamento intencional.

3. **Qual a diferença entre `OrderId` e `MessageId`?**
   `OrderId` identifica o fato de negócio; `MessageId` identifica a mensagem técnica que transporta esse fato.

4. **Qual era a limitação do fluxo persistir-depois-publicar?**
   PostgreSQL e Service Bus não participavam de uma transação única; um pedido podia ser confirmado sem evento.

5. **A Outbox altera o JSON de `OrderCreated`?**
   Não. Ela altera o momento e o local de armazenamento da publicação, preservando payload, topic e metadata do evento.

## Cenários de entrevista

1. **“A API retornou 500, mas o pedido existe no banco. O que você investigaria no fluxo original?”**
   Raciocínio sugerido: verificar se a persistência terminou antes da falha de publish. O desenho original podia deixar o pedido confirmado e o evento ausente; não se deve presumir rollback distribuído.

2. **“Um consumidor quer ler o `Subject` dentro do JSON. Você mudaria o contrato?”**
   Raciocínio sugerido: usar metadata nativa para classificação e filtro. Só adicionar ao payload um valor necessário ao negócio; duplicar metadata de transporte cria fontes de verdade concorrentes.

3. **“Por que não esperar Payment e Inventory antes de responder `201`?”**
   Raciocínio sugerido: isso acoplaria a transação HTTP a consumidores assíncronos e seus tempos de falha. `201` confirma a criação conforme o contrato; os efeitos derivados são eventualmente consistentes.

## Key takeaways

- `OrderCreated` representa o fato de negócio de que um pedido foi criado, em um contrato de integração estável.
- O payload contém apenas dados do evento; metadata técnica fica no envelope nativo do Service Bus.
- `OrderId` é a identidade de correlação de negócio; `MessageId` identifica a mensagem técnica.
- O fluxo original persistia primeiro e publicava depois, criando uma limitação de dual-write documentada.
- Eventual consistency permite que consumidores downstream processem fora da transação HTTP.
- Subject estável e topic `order-events` prepararam o fan-out de Payment, Inventory e Notification.
- A Outbox posterior mudou a publicação para uma intenção durável sem alterar o contrato `OrderCreated`.
