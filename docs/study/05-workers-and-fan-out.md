# Workers e fan-out no CloudOrders

Uma publicação de `OrderCreated` descreve um fato; ela não determina que exista um único processamento. No CloudOrders, o mesmo evento pode iniciar três reações independentes: pagamento, reserva de inventário e notificação. A separação em workers e subscriptions torna essa independência visível no código e no Azure Service Bus.

Este capítulo parte dos changes arquivados `add-payment-worker`, `add-inventory-worker` e `add-notification-worker`. Cada um adicionou um projeto e uma subscription, sem provider externo ou framework genérico. O estado atual também possui idempotência, classificação de falhas, retry/DLQ e observabilidade; essas evoluções são identificadas como posteriores.

## Por que três workers

Payment, Inventory e Notification têm ritmos, falhas e responsabilidades diferentes. Um gateway de pagamento pode exigir credenciais, timeout e auditoria; um sistema de inventário pode ter disponibilidade e regras próprias; email ou SMS pode depender de outro provider. Mesmo quando todos reagem ao mesmo pedido, não é saudável transformar a API ou um worker em coordenador síncrono dos três.

Cada worker é um processo independente, com um projeto próprio na solution:

- `CloudOrders.Payment.Worker` consome `payment`;
- `CloudOrders.Inventory.Worker` consome `inventory`;
- `CloudOrders.Notification.Worker` consome `notification`.

As subscriptions recebem cópias diferentes da publicação em `order-events`. Se Payment estiver indisponível, sua mensagem pode ficar pendente sem impedir que Inventory e Notification processem as próprias cópias. Essa é a diferença entre fan-out e uma única fila compartilhada.

## A topologia de fan-out

```text
                         OrderCreated
                              |
                              v
                     Topic: order-events
                              |
          +-------------------+-------------------+
          |                   |                   |
          v                   v                   v
      payment             inventory           notification
      filter:             filter:             filter:
      OrderCreated        OrderCreated        OrderCreated
          |                   |                   |
          v                   v                   v
    Payment.Worker     Inventory.Worker     Notification.Worker
          |                   |                   |
          v                   v                   v
 simulated payment    simulated reservation  simulated notification
```

Os filtros das três subscriptions são equivalentes a `sys.Label = 'CloudOrders.Orders.OrderCreated'`. A regra `$Default`, que aceitaria qualquer mensagem, deve ser removida para que a topologia não misture o probe ou outros eventos com os consumidores de negócio. O `messaging-probe` continua separado e filtrado para `CloudOrders.Messaging.Probe`.

## Wrapper de Service Bus versus processor

Cada worker tem duas responsabilidades separadas.

O wrapper — `ServiceBusPaymentWorker`, `ServiceBusInventoryWorker` ou `ServiceBusNotificationWorker` — é a borda de transporte. Ele herda de `BackgroundService`, cria um `ServiceBusProcessor` de longa duração para o topic e subscription configurados, registra os callbacks de mensagem e de erro, inicia o processor e o encerra quando o host recebe cancelamento. Também escolhe `AutoCompleteMessages = false`, `ReceiveMode = PeekLock` e `MaxConcurrentCalls = 1`.

O wrapper recebe `ProcessMessageEventArgs`, extrai body, `MessageId`, `CorrelationId`, `Subject`, `ContentType` e `DeliveryCount`, e passa valores simples ao processor. Completion e, no estado atual, dead-letter são delegates. Assim, o processor não referencia Azure SDK.

O `PaymentMessageProcessor`, `InventoryMessageProcessor` e `NotificationMessageProcessor` são a borda da mensagem e da regra de negócio. Eles validam o contrato, desserializam `OrderCreated`, chamam o processador específico e só depois pedem a completion. Cada `*Processing.cs` contém a simulação de negócio. Essa divisão permite testar o fluxo com bytes, strings e callbacks falsos, sem iniciar um `ServiceBusProcessor` real.

## `AutoCompleteMessages=false`, PeekLock e completion

Com PeekLock, receber uma mensagem não a remove imediatamente do broker: ela fica sob lock temporário. `CompleteMessageAsync` reconhece explicitamente o processamento concluído.

O código desabilita auto-completion porque quer controlar a ordem:

```text
receber sob lock
      |
      v
validar subject/content type e desserializar
      |
      v
executar simulação de negócio
      |
      v
CompleteMessageAsync
```

Se validação, desserialização ou negócio falhar, o worker não deve completar. Nos changes iniciais, não havia retry, abandon ou DLQ customizados; a exceção seguia o comportamento do processor/broker. No estado atual, a classificação de falhas e o limite de `MaxDeliveryCount = 5` configurado no broker são detalhados no [capítulo 07](./07-retry-and-dead-letter.md).

Completion ainda pode falhar depois do negócio, causando redelivery. O Inbox por `(ConsumerName, MessageId)` evita reexecutar o processamento já commitado e tenta o settlement novamente; o ciclo transacional completo está no [capítulo 06](./06-idempotent-consumers-and-inbox.md). Isso não é exactly-once fornecido pelo PeekLock.

## Por que `MaxConcurrentCalls=1` no começo

Processar uma mensagem por vez foi uma escolha pedagógica e operacional. Com `MaxConcurrentCalls = 1`, é mais simples observar logs, comparar entrega e resultado, e entender “processar, depois completar”. Também reduz ruído no smoke test e evita que concorrência mascare contrato ou configuração.

Não é uma regra de produção. Ao aumentar concorrência, a equipe precisa revisar throughput, locks, transações, idempotência, rate limits e efeitos externos. O wrapper ainda mantém essa configuração inicial.

## O que cada worker simula

| Worker | Subscription | Simulação atual | O que deliberadamente não existe |
| --- | --- | --- | --- |
| Payment | `payment` | Processamento de `Pending` com `simulated-payment-{OrderId:N}`. | Gateway real, banco de pagamentos, schema e chamada externa. |
| Inventory | `inventory` | Reserva de `Pending` com `simulated-reservation-{OrderId:N}`. | Sistema de inventário real, persistência e banco próprios. |
| Notification | `notification` | Notificação de `Pending`, canal `simulated`, referência `simulated-notification-{OrderId:N}`. | Email, SMS, HTTP externo, provider e persistência. |

Os três processadores recebem o mesmo contrato `OrderCreated`, mas têm interfaces locais — `IPaymentProcessor`, `IInventoryProcessor` e `INotificationProcessor`. Cada um aceita somente `Pending` na simulação atual. Um status diferente é uma falha de regra de negócio da entrada daquele worker, não um erro de JSON.

As referências determinísticas não dependem de relógio, rede, aleatoriedade ou banco: o mesmo `OrderId` gera o mesmo valor. Isso facilita logs, smoke tests e asserts unitários, embora não prove o comportamento de um provider real.

## Falha de um consumer não bloqueia os outros

O broker mantém uma entrega por subscription. Cópias em `payment` e `inventory` podem conter o mesmo `MessageId`, mas têm lock, settlement e processamento independentes. Payment pode falhar enquanto Inventory completa sua cópia.

O producer não espera os três resultados e não precisa saber qual worker está disponível. A consequência é eventual consistency: por algum tempo, Payment pode estar processado, Inventory pendente e Notification em retry. Essa assimetria é uma propriedade do desenho, não uma inconsistência acidental.

## Três classes de falha

É útil não tratar todo erro como “mensagem falhou”. Os processors atuais separam:

1. **Falha de transporte:** Service Bus não conecta, o lock expira, o receiver falha ou `CompleteMessageAsync`/dead-letter falha. O wrapper registra o erro no callback do processor.
2. **Falha de contrato:** subject, content type, body, JSON ou `MessageId` não atendem ao contrato. Hoje, recebe `MessageContractViolation` sem completion.
3. **Falha de regra de negócio:** o JSON é válido, mas o status não é suportado pela simulação. Uma exceção como `UnsupportedInventoryOrderStatusException` pode ir para DLQ com `UnsupportedBusinessStatus`, sem completion.

Falhas transitórias de negócio, PostgreSQL/inbox ou operações desconhecidas não devem ser convertidas arbitrariamente em erro permanente. Elas ficam sem completion para permitir redelivery. Essa classificação impede que um problema temporário seja descartado como poison message ou que uma mensagem inválida seja repetida indefinidamente.

## Walkthrough: um pedido bem-sucedido

Considere `OrderId = O` e `status = Pending`.

1. A API cria o pedido e o `OrderCreated`. Originalmente, persistia e publicava no topic; hoje, o Outbox torna a publicação posterior e durável. O wire contract permanece igual.
2. Service Bus aceita a mensagem em `order-events` com `MessageId`, `CorrelationId = O`, subject `CloudOrders.Orders.OrderCreated` e content type `application/json`.
3. O filtro de cada subscription cria uma cópia; `messaging-probe` rejeita o subject.
4. Cada wrapper recebe sua cópia sob PeekLock e entrega dados simples ao processor.
5. Cada processor valida metadata, desserializa e verifica `Pending`.
6. Os resultados são `simulated-payment-O`, `simulated-reservation-O` e, para Notification, canal `simulated` mais `simulated-notification-O`.
7. Cada consumer registra `OrderId`, `MessageId` e `CorrelationId` e completa somente sua cópia. Um erro em Notification não desfaz Payment ou Inventory.

## Identidade consistente nos três consumidores

`OrderId` identifica o negócio e aparece nas referências e logs de sucesso. `MessageId` identifica a mensagem lógica do Service Bus e é recebido do envelope nativo por cada wrapper; `DeliveryCount` diferencia suas redeliveries. `CorrelationId` liga a mensagem ao pedido e aparece nos logs junto dos dois outros valores.

Essa consistência permite seguir um pedido entre processos sem repetir o body em cada log. O mesmo `MessageId` pode ser observado nas três subscriptions, enquanto cada consumer mantém sua identidade e resultado.

## Por que não criar um framework genérico de workers

Os três wrappers têm estrutura parecida, mas a repetição é pequena e explícita. Um framework genérico teria de decidir contratos, settlement, erros, concorrência, logging, DLQ e idempotência antes de os requisitos existirem, escondendo diferenças entre os workers.

O projeto reutiliza `ServiceBusClient`, `ServiceBusMessagingOptions`, registro de infraestrutura e `OrderCreated`. Cada worker mantém host, processor e testes próprios; uma política específica pode surgir no worker correto.

## Evolução para providers reais

A simulação é um seam, não uma integração pronta. Payment pode trocar por um adapter de gateway, Inventory chamar o estoque e Notification escolher email ou SMS. Os processors precisarão de timeouts, autenticação, limites, efeitos parciais e idempotência adequada ao provider.

O estado atual já demonstra parte dessa preparação: inbox usa consumer e `MessageId`, completion ocorre depois do commit e logs separam outcomes. Ainda não há exactly-once com serviços externos; será preciso decidir chaves idempotentes, reconciliação de timeout e recuperação de DLQ.

## Falhas operacionais comuns

- **Subscription sem evento:** conferir existência no topic `order-events`, filtro exato `sys.Label = 'CloudOrders.Orders.OrderCreated'` e remoção de `$Default`.
- **Eventos errados em todos:** uma TrueFilter pode aceitar probe e negócio em todas as subscriptions.
- **Falha ao iniciar:** validar `ConnectionStrings:ServiceBus`, `Messaging:TopicName` e `Messaging:SubscriptionName`. O README usa User Secrets; `.env` é do Compose do PostgreSQL.
- **Mensagem na DLQ:** diferenciar `MessageContractViolation` de `UnsupportedBusinessStatus` pela razão e descrição.
- **Mensagem reaparece:** investigar lock expirado ou falha em `CompleteMessageAsync`; redelivery é possível e o inbox deve evitar reprocessamento.
- **Um worker parado:** fan-out permite que os outros sigam. Investigue a subscription, métricas e logs afetados, não uma suposta transação entre os três.

O smoke test documentado no README inicia API, Payment, Inventory e Notification em processos separados, cria um pedido e compara os logs. A evidência esperada é o mesmo pedido e correlação em três workers, três referências determinísticas e ausência de mensagens ativas após completion. O probe é testado separadamente para confirmar que seu filtro continua isolado.

## Mental model

Imagine `order-events` como uma prensa que produz três cópias do mesmo cartão. O cartão identifica o pedido e descreve `Pending`; cada caixa — Payment, Inventory ou Notification — abre sua cópia e executa uma reação diferente. O carteiro confirma cada caixa separadamente. Um problema em uma caixa não apaga as outras.

O wrapper cuida do carteiro e do lock; o message processor interpreta o cartão; a simulação representa o trabalho especializado. A fila não garante que o trabalho externo terminou: ela apenas fornece uma entrega que o consumer precisa processar e confirmar.

## Glossário

- **Worker:** processo hospedado que executa processamento contínuo de mensagens.
- **Wrapper de transporte:** código que configura `ServiceBusProcessor` e traduz callbacks do SDK.
- **Message processor:** componente que valida, desserializa e coordena o processamento de uma mensagem.
- **Fan-out:** distribuição de uma publicação para várias subscriptions independentes.
- **Subscription:** cópia lógica filtrada de um topic.
- **PeekLock:** entrega com lock temporário antes do settlement.
- **Completion:** confirmação explícita de processamento bem-sucedido.
- **Contract failure:** mensagem que não respeita subject, content type, body ou JSON esperados.
- **Business-rule failure:** mensagem válida cujo conteúdo não é suportado pelo processamento.
- **Redelivery:** nova entrega de uma mensagem que não foi completada ou cujo settlement falhou.
- **Deterministic simulation:** processamento sem I/O ou aleatoriedade que retorna o mesmo resultado para a mesma entrada.

## Perguntas de revisão

1. **Por que Payment, Inventory e Notification não compartilham uma única subscription?**
   Porque cada função precisa de uma cópia independente e de isolamento de falhas; uma única subscription faria os consumidores competirem pela mesma mensagem.

2. **O que o wrapper de Service Bus deve fazer?**
   Manter o processor, configurar PeekLock/concurrency, extrair metadata e conectar callbacks de completion e erro ao processor de mensagem.

3. **Por que a completion fica depois da simulação?**
   Para não confirmar uma mensagem antes de o contrato ser entendido e o processamento terminar com sucesso.

4. **O que `MaxConcurrentCalls=1` facilita?**
   Observação, debugging e aprendizagem da ordem de processamento, reduzindo concorrência enquanto a topologia é validada.

5. **Como uma falha de Payment afeta Inventory?**
   A cópia de Payment pode permanecer unsettled ou ir para sua política de falha; a cópia independente de Inventory continua com seu próprio ciclo.

## Cenários de entrevista

1. **“Um pagamento demora e a equipe quer fazer a API esperar antes de responder.”**
   Raciocínio sugerido: separar criação do pedido de efeitos downstream. A API publica o evento; o worker de Payment possui seu próprio tempo e falha. Esperar acoplaria o request a um provider externo e quebraria o benefício do fan-out.

2. **“Inventory recebe o mesmo `MessageId` duas vezes. O que deve ser analisado?”**
   Raciocínio sugerido: verificar se a primeira execução completou o inbox de Inventory e se a segunda pode pular o negócio e completar a entrega. Não usar o `MessageId` sozinho para suprimir Payment ou Notification, pois a identidade é por consumer.

3. **“Um JSON válido tem status `Cancelled`, mas o worker só suporta `Pending`.”**
   Raciocínio sugerido: isso não é falha de desserialização. É regra de negócio não suportada; deve seguir a classificação permanente vigente e não ser completado como se tivesse sido processado.

## Key takeaways

- Workers separados permitem que Payment, Inventory e Notification evoluam e falhem independentemente.
- Uma publicação em `order-events` gera cópias para subscriptions filtradas, não uma única disputa entre consumidores.
- O wrapper conhece Azure Service Bus; o processor conhece contrato, regra e simulação.
- `AutoCompleteMessages=false`, PeekLock e completion explícito tornam a ordem de settlement visível.
- `MaxConcurrentCalls=1` foi um bom primeiro passo para aprender e diagnosticar a topologia.
- Referências determinísticas tornam testes, logs e smoke tests reproduzíveis.
- Falhas de transporte, contrato e negócio exigem tratamentos diferentes.
- Não foi necessário criar um framework genérico: os seams concretos já eram suficientes.
- Providers reais exigirão idempotência, timeouts, segurança, reconciliação e observabilidade adicionais.
