# Consumers idempotentes e o Inbox no CloudOrders

Mensagens em sistemas distribuídos não são como chamadas de método dentro do mesmo processo. Um worker pode receber uma mensagem, executar seu trabalho e cair antes de confirmar ao broker que terminou. Para o broker, a confirmação não aconteceu; portanto, a mensagem pode voltar. O CloudOrders usa esse caso para ensinar uma ideia essencial: entrega duplicada é normal em um sistema at-least-once, mas efeito de negócio duplicado precisa ser controlado pelo consumer.

Este capítulo se baseia no change arquivado `add-idempotent-consumers`, no estado atual de `processed_messages`, nos três workers de negócio e nos testes unitários e de integração. A simulação atual não chama gateway de pagamento, sistema de estoque ou provider de notificações. Por isso, o Inbox implementado é uma solução concreta para os efeitos simulados e um limite didático explícito para evoluções futuras.

## Entrega duplicada não é o mesmo que efeito duplicado

O Service Bus usa o modelo de entrega at-least-once nas subscriptions do CloudOrders. Em termos práticos, o broker tenta entregar a mensagem pelo menos uma vez, mas uma mesma mensagem pode ser entregue novamente. Isso pode ocorrer quando:

- o processo cai depois do processamento e antes de `CompleteMessageAsync`;
- o lock de PeekLock expira enquanto o worker ainda trabalha;
- a chamada de completion falha ou perde a resposta;
- a aplicação cancela ou perde conectividade durante o settlement.

Uma duplicata de transporte é uma segunda observação do mesmo envelope, identificada pelo mesmo `MessageId`. Ela ainda não significa que o efeito foi repetido. O problema aparece quando o consumer chama de novo a operação de negócio antes de descobrir que aquela mensagem já foi concluída.

Considere Payment: receber duas vezes `m-123` é duplicação de entrega; cobrar duas vezes o cartão é duplicação de efeito. Para Inventory, o efeito seria reservar duas vezes a mesma unidade; para Notification, enviar duas vezes. O broker redeliverá, mas não conhece essas regras. Cabe ao consumer tornar sua operação segura.

## Idempotência é responsabilidade do consumer

Uma operação é idempotente quando repeti-la com a mesma identidade lógica produz o mesmo resultado observável que executá-la uma vez. Em um consumidor, isso normalmente exige três decisões:

1. qual chave identifica a tentativa;
2. onde armazená-la de forma durável;
3. quando declarar a operação concluída.

O worker não deve completar apenas por ler o JSON: precisa coordenar validação, processamento, registro de sucesso e settlement. O `MessageId` também não é uma identidade global de negócio; seu uso é local ao consumer.

O `PaymentMessageProcessor`, por exemplo, valida o contrato de `OrderCreated` e chama:

```text
ExecuteOnceAsync(
    ConsumerIdentities.Payment,
    messageId,
    processar pagamento simulado)
```

Inventory e Notification fazem o mesmo com `ConsumerIdentities.Inventory` e `ConsumerIdentities.Notification`. A interface é genérica o suficiente para devolver o receipt atual, sem acoplar o mecanismo de idempotência às classes `SimulatedPaymentReceipt`, `SimulatedInventoryReservation` ou `SimulatedNotificationReceipt`.

## O Inbox Pattern no CloudOrders

O Inbox Pattern registra no lado consumidor as mensagens que já foram processadas com sucesso. No CloudOrders, essa infraestrutura é a tabela PostgreSQL `processed_messages`, mapeada pela entidade `ProcessedMessage` no `CloudOrdersDbContext`.

O modelo contém:

- `consumer_name`: a identidade lógica estável do consumer;
- `message_id`: o `MessageId` nativo do Azure Service Bus, armazenado sem substituição;
- `state`: `Processing` durante a tentativa transacional e `Completed` quando o sucesso é persistido;
- `started_at_utc`: início da tentativa;
- `completed_at_utc`: momento do sucesso persistido.

O `IProcessedMessageStore`, em `CloudOrders.Application.Messaging`, é a porta usada pelos processors. Seu resultado `ProcessedMessageExecution<T>` informa se a execução foi uma duplicata (`WasAlreadyProcessed`) e, no caminho novo, transporta o resultado da operação. `EfProcessedMessageStore`, em Infrastructure, é o adaptador que conhece EF Core, PostgreSQL e transações.

Os workers não criam tabelas diferentes nem mantêm um `HashSet` próprio. Cada `Program.cs` chama `AddProcessedMessagePersistence`, que registra `IDbContextFactory<CloudOrdersDbContext>` e o store. A factory evita que processors singleton mantenham um `DbContext` com escopo inadequado.

### Por que a chave é `(ConsumerName, MessageId)`

O banco define uma primary key composta por `(consumer_name, message_id)`. Essa combinação resolve dois problemas diferentes:

1. dentro de um consumer, o mesmo `MessageId` só pode adquirir uma claim;
2. entre consumers, o evento continua disponível uma vez para cada reação.

Uma publicação `OrderCreated` é fan-out para Payment, Inventory e Notification. É legítimo que o `MessageId` `m-123` apareça três vezes na tabela:

```text
(Payment,      m-123) -> Completed
(Inventory,    m-123) -> Completed
(Notification, m-123) -> Completed
```

Se a chave fosse somente `MessageId`, o primeiro worker a gravar `m-123` poderia impedir os outros dois. Isso confundiria identidade de mensagem com identidade de processamento. `OrderId` e `CorrelationId` também não substituem a chave: eles representam o negócio e a correlação, mas não expressam qual subscription e qual reação já foram concluídas. O change centralizou os valores `Payment`, `Inventory` e `Notification` em `ConsumerIdentities` para evitar strings locais com diferenças de caixa, grafia ou significado.

## Claim atômica e a race condition

Uma implementação ingênua faria:

```text
SELECT existe(consumer, message)
se não existe:
    executa negócio
    INSERT consumer/message
```

Duas entregas concorrentes podem executar o `SELECT` antes que qualquer uma faça o `INSERT`. Ambas concluem que a mensagem é nova e ambas executam o negócio. Isso é a clássica check-then-insert race.

O `EfProcessedMessageStore` começa por uma operação atômica de PostgreSQL:

```sql
INSERT INTO processed_messages
    (consumer_name, message_id, state, started_at_utc, completed_at_utc)
VALUES
    (@consumerName, @messageId, @state, @startedAtUtc, NULL)
ON CONFLICT (consumer_name, message_id) DO NOTHING
RETURNING 1;
```

O retorno `1` informa que a transação adquiriu a claim. Sem retorno, houve conflito. O PostgreSQL usa a primary key para arbitrar a concorrência; uma tentativa conflitante aguarda a transação que possui a chave terminar. Depois do conflito, o store lê o estado persistido: `Completed` significa duplicata segura. Um estado inesperado `Processing` é tratado como falha de persistência, não como autorização para executar duas vezes.

A claim é uma linha criada dentro de uma transação, não um “lock mágico” permanente. Enquanto a transação está aberta, outra tentativa não adquire a chave; se o callback falha, o rollback remove a claim e uma entrega posterior pode tentar novamente.

## Ciclo de vida das transações

### Primeira entrega bem-sucedida

```text
Service Bus entrega sob PeekLock
             |
             v
Processor valida e desserializa OrderCreated
             |
             v
BEGIN PostgreSQL
  INSERT ... ON CONFLICT DO NOTHING -> claim adquirida
  executa operação de negócio
  UPDATE state = Completed
COMMIT
             |
             v
CompleteMessageAsync
```

O `Completed` só é durável no commit. Depois, o processor retorna ao wrapper, que chama `CompleteMessageAsync`; o broker não é confirmado antes do sucesso persistido.

### Entrega duplicada já concluída

```text
Service Bus redeliverá a mesma mensagem
             |
             v
BEGIN PostgreSQL
  INSERT ... ON CONFLICT -> conflito
  lê (consumer, MessageId) = Completed
COMMIT sem executar negócio
             |
             v
CompleteMessageAsync
```

`WasAlreadyProcessed = true` faz o processor registrar o duplicate-skip e não chamar o negócio. A duplicata ainda deve ser completada, ou continuará voltando.

### Negócio falha antes do commit

Se o callback lança uma exceção, o store faz rollback. Não há uma linha `Completed` para mascarar a falha e o wrapper não chama completion. A mensagem permanece unsettled, seguindo a política de redelivery vigente. Uma nova entrega pode adquirir a claim e tentar o negócio novamente.

Falhas de contrato, como body vazio ou JSON inválido, acontecem antes do Inbox e não criam estado. A política posterior classifica-as como `MessageContractViolation`, não como sucesso.

## A janela crítica: negócio commitado, settlement falho

O banco PostgreSQL e o Service Bus não participam da mesma distributed transaction. Portanto, existe uma janela deliberada entre o commit do Inbox e a confirmação do broker:

```text
Entrega m-123 sob lock
       |
       v
Inbox + operação simulada -> Completed
       |
       v
COMMIT PostgreSQL
       |
       v
CompleteMessageAsync falha
       |
       v
Service Bus redeliverá m-123
       |
       v
INSERT conflita; estado Completed
       |
       v
Pula negócio e tenta CompleteMessageAsync novamente
```

Por isso a conclusão é persistida antes do settlement. A redelivery não é perdida nem refaz a operação. Os testes dos workers cobrem completion falho e verificam o salto do processor na entrega posterior.

A proteção é especialmente segura porque os três processadores simulados não possuem I/O externo. O store coordena simulação e `Completed` na mesma transação curta; isso não torna efeitos externos exactly-once.

## O que os testes demonstram

Os testes unitários usam `InMemoryProcessedMessageStore` como fake da porta. Verificam primeira entrega, duplicate-skip, completion da duplicata, falha sem completion e identidades corretas, sem iniciar Azure Service Bus.

Os testes de integração `ProcessedMessageStoreTests` usam a fixture PostgreSQL com Testcontainers e o store real. Eles demonstram que:

- o estado `Completed` sobrevive à criação de outro service provider;
- o mesmo `MessageId` pode ser processado uma vez por cada um dos três consumers;
- uma exceção do negócio faz rollback e permite retry posterior;
- duas claims concorrentes para o mesmo consumer e `MessageId` invocam o callback apenas uma vez.

O fake testa o processor; PostgreSQL real testa constraint, transação e arbitragem de concorrência. Nenhum exige namespace Azure ativo.

## Limitações e trade-offs

O Inbox melhora muito a segurança contra redelivery, mas impõe custos e limites:

- PostgreSQL vira dependência operacional de cada worker: sem banco, não há claim.
- A transação mantém o callback dentro da unidade de trabalho. Isso serve às simulações rápidas, mas não a uma chamada lenta de gateway.
- A tabela guarda identidade e estado, não o histórico de pagamento, reserva ou notificação; ela não substitui persistência de negócio.
- O repositório não mostra política de retenção/limpeza. Em produção, volume, retenção e auditoria seriam decisões explícitas.
- Não há exactly-once universal fora do PostgreSQL.

Com provider real, há cenários ambíguos: se o gateway cobrar e o processo cair antes de `Completed`, a redelivery pode cobrar de novo; se `Completed` vier antes do gateway, pode haver linha concluída sem cobrança. A solução exige idempotency key do provider, estado de negócio, consulta/reconciliação ou desenho equivalente. O `MessageId` só ajuda externamente se o provider respeitar a mesma identidade.

## Inbox + Outbox: complementary patterns

Inbox e Outbox resolvem lados diferentes da fronteira distribuída. O Outbox protege o produtor: a mudança de negócio e o registro do evento a publicar entram na mesma transação do banco. O dispatcher publica depois, fora do request. O Inbox protege o consumidor: a claim, a operação atual e o registro de processamento concluído ficam coordenados no PostgreSQL antes do settlement do broker.

No estado atual do CloudOrders, `OrderCreated` usa Outbox para tornar a publicação durável, e Payment, Inventory e Notification usam Inbox para tornar a redelivery do consumer segura. Uma sequência conceitual é:

```text
API: Order + Outbox COMMIT
             |
             v
Dispatcher -> Service Bus topic
             |
       fan-out para subscriptions
             |
             v
Cada worker: Inbox claim + negócio + Completed COMMIT
             |
             v
       CompleteMessageAsync
```

O Outbox não elimina a necessidade de Inbox, e o Inbox não garante que o evento foi publicado. Juntos, eles reduzem dois dual writes distintos. A explicação detalhada do dispatcher, das tentativas de publicação e dos campos do Outbox pertence ao capítulo próprio; aqui, a ideia essencial é que a confiabilidade do produtor e a deduplicação do consumidor são responsabilidades complementares.

## Mental model

Imagine uma portaria com três livros independentes, um para Payment, um para Inventory e um para Notification. A mesma encomenda pode ser apresentada a cada portaria, porque cada equipe tem um trabalho próprio. Em cada livro, o par `(ConsumerName, MessageId)` funciona como um carimbo: se o carimbo `Completed` já existe, a equipe não repete o trabalho, mas ainda confirma que viu a encomenda.

A transação PostgreSQL é o momento em que a equipe reserva a linha, executa o trabalho e registra o carimbo. O `CompleteMessageAsync` é o aviso separado ao mensageiro. Se o aviso se perde, a encomenda volta, o carimbo é consultado e somente o aviso é repetido.

## Glossário

- **At-least-once:** modelo em que uma mensagem pode ser entregue novamente até ser confirmada.
- **Idempotência:** propriedade de repetir uma operação sem produzir efeito adicional indesejado.
- **Inbox Pattern:** registro durável no consumer para reconhecer mensagens já processadas.
- **Claim:** aquisição exclusiva da oportunidade de processar uma chave.
- **Consumer identity:** nome lógico da reação, como `Payment` ou `Inventory`.
- **MessageId:** identidade técnica nativa da mensagem no Service Bus.
- **PeekLock:** modo em que a mensagem fica sob lock temporário antes do settlement.
- **Settlement:** ação que confirma, abandona ou envia a mensagem para outro estado no broker; no caminho bem-sucedido, é `CompleteMessageAsync`.
- **Redelivery:** nova entrega após ausência de completion, falha de lock ou falha de settlement.
- **Exactly-once:** promessa forte de execução única; o Inbox do CloudOrders não a fornece para efeitos externos arbitrários.
- **Dual write:** necessidade de atualizar dois sistemas sem uma transação única entre eles.

## Perguntas de revisão

1. **Por que o Service Bus pode entregar a mesma mensagem duas vezes?**  
   Porque o processamento e a confirmação são etapas separadas. Queda, expiração do lock ou falha no settlement podem fazer o broker tentar novamente.

2. **Por que `MessageId` sozinho não pode ser a chave do Inbox?**  
   Porque o mesmo evento é processado legitimamente por Payment, Inventory e Notification. A chave precisa incluir a identidade do consumer.

3. **O que `INSERT ... ON CONFLICT DO NOTHING RETURNING 1` evita?**  
   Evita a race condition de consultar e inserir em passos separados. A primary key e o PostgreSQL elegem uma única claim concorrente.

4. **O que acontece quando o negócio falha dentro de `ExecuteOnceAsync`?**  
   A transação sofre rollback, não fica um `Completed` e a mensagem não é completada. Uma entrega posterior pode tentar novamente.

5. **O Inbox garante exactly-once para um gateway de pagamento?**  
   Não. Ele coordena a simulação e o estado PostgreSQL, mas um efeito externo pode ocorrer antes de um crash. O provider precisa de idempotency key, reconciliação ou estado próprio.

## Cenários de entrevista

1. **“Dois processos receberam o mesmo `MessageId` para Inventory e ambos estão prestes a reservar estoque. Como raciocinar?”**  
   Verifique se ambos usam `ConsumerIdentities.Inventory` e o mesmo `MessageId`, e se o claim começa com a inserção atômica na primary key composta. Uma transação adquire a chave; a outra aguarda o conflito, observa `Completed` e não chama o negócio.

2. **“Payment ficou `Completed` no PostgreSQL, mas o log mostra falha em `CompleteMessageAsync`. Isso é corrupção?”**  
   Não necessariamente. É a janela esperada entre banco e broker. A mensagem pode ser redeliverada; a próxima entrega deve detectar o Inbox concluído, pular o pagamento e tentar o settlement novamente.

3. **“O time quer colocar uma chamada lenta ao gateway dentro da mesma transação do Inbox para obter exactly-once.”**  
   Questione a estratégia. A transação curta funciona para a simulação sem I/O, mas manter lock e conexão durante uma chamada externa não torna as duas operações atômicas. Prefira contrato idempotente do provider, estado de negócio, Outbox/command específico e reconciliação de resultados ambíguos.

## Key takeaways

- At-least-once torna redelivery uma possibilidade normal, não uma exceção impossível.
- Duplicata de mensagem só vira duplicata de efeito quando o consumer repete o negócio sem controle.
- O Inbox do CloudOrders usa PostgreSQL e a chave composta `(ConsumerName, MessageId)`.
- `INSERT ... ON CONFLICT DO NOTHING RETURNING 1` fornece uma claim atômica contra concorrência.
- Payment, Inventory e Notification podem processar o mesmo evento uma vez cada, sem se suprimirem.
- O estado `Completed` é commitado antes de `CompleteMessageAsync`, protegendo o caso de settlement falho.
- Falhas de negócio fazem rollback e deixam a mensagem disponível para retry; falhas de contrato ocorrem antes do Inbox.
- Os testes unitários isolam processors; Testcontainers valida transações, persistência e concorrência reais do PostgreSQL.
- Inbox e Outbox são padrões complementares, não uma promessa universal de distributed exactly-once.
- Providers externos exigirão idempotency keys, reconciliação, observabilidade e políticas de retenção adequadas.
