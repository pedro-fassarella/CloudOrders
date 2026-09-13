# Retry e Dead Letter no CloudOrders

Falha de processamento não é uma categoria única. Uma mensagem pode estar malformada, conter um status que o worker ainda não suporta, encontrar um PostgreSQL indisponível ou simplesmente perder o lock no broker. Tratar todos esses casos como “tentar novamente” produz ruído, latência e filas difíceis de operar.

O CloudOrders escolheu uma política deliberadamente pequena: erros permanentes conhecidos vão imediatamente para a subscription DLQ; erros transitórios ficam sem settlement e permitem redelivery pelo Azure Service Bus; nas subscriptions de negócio provisionadas conforme o README, depois de `MaxDeliveryCount = 5`, o broker move a mensagem para a DLQ. Não há Polly, loop de retry local nem `AbandonMessageAsync` explícito no código de negócio.

Este capítulo usa o change arquivado `add-retry-and-dead-letter-handling`, `MessageFailureHandling`, os três workers, os testes e o smoke test documentado no README. A política descrita é uma evolução posterior ao primeiro walking skeleton e aos workers iniciais: o comportamento inicial deixava qualquer exceção unsettled, enquanto o estado atual distingue falhas permanentes de falhas retryable.

## Retry, redelivery e dead-lettering

Esses termos são próximos, mas descrevem mecanismos diferentes:

- **Retry local:** o mesmo handler captura uma falha e executa novamente antes de terminar a entrega atual. Pode usar delay, backoff ou Polly.
- **Redelivery:** o handler termina com exceção, cancelamento ou sem completar a mensagem; o broker entrega novamente em outra tentativa.
- **Dead-lettering:** a mensagem é retirada do fluxo normal e colocada na DLQ da subscription, com uma razão e uma descrição para diagnóstico.

No CloudOrders, a repetição de uma falha transitória é principalmente responsabilidade do broker. Isso mantém uma única contagem de delivery no Service Bus e evita que um worker esconda várias tentativas locais dentro de um único lock. A aplicação só faz settlement manual quando já sabe que a mensagem não será corrigida por repetição.

O SDK pode ter retry próprio para operações transitórias de transporte antes de uma exceção chegar ao handler. Isso não é retry do processamento de `OrderCreated`; o projeto não envolve o callback em Polly.

## A classificação de falhas

`MessageFailureHandling.cs`, na camada Application, define `PermanentMessageFailureKind` com duas categorias: `ContractViolation` e `UnsupportedBusinessStatus`. `PermanentMessageFailureException` carrega essa classificação; `MessageDeadLetterDetailsFactory` converte o tipo em uma razão estável:

- `ContractViolation` -> `MessageContractViolation`;
- `UnsupportedBusinessStatus` -> `UnsupportedBusinessStatus`.

Os processors de Payment, Inventory e Notification validam subject, content type, body, JSON e `MessageId` antes de entrar no Inbox. Se algo estiver inválido, a mensagem não se tornará válida com mais quatro tentativas. O processor usa o delegate de dead-letter recebido pelo wrapper e não chama completion.

Um JSON válido com status `Cancelled` é outro caso. As simulações só suportam `Pending`; a falha tipada ocorre dentro de `ExecuteOnceAsync`, o Inbox sofre rollback e o processor pede `UnsupportedBusinessStatus`. Não é falha de transporte nem JSON inválido.

Já uma indisponibilidade do PostgreSQL, uma falha transitória do callback, uma exceção desconhecida, a perda do lock ou uma falha de settlement não é classificada como permanent failure. A exceção propaga, a mensagem não é completada e o broker pode redeliver.

## Decision tree

```text
Mensagem entregue sob PeekLock
            |
            v
Contrato válido? (ID, subject, content type, body, JSON)
       | sim                         | não
       v                             v
Status suportado?              DLQ manual
       | sim                   reason: MessageContractViolation
       |                  sem CompleteMessageAsync
       v
Processamento e Inbox concluíram?
       | sim                         | não
       v                             v
Commit Completed                 exceção propaga
       |                         sem completion/sem DLQ manual
       v                             |
CompleteMessageAsync              v
       |                    Service Bus redelivery
       v                             |
Mensagem concluída          após cinco deliveries -> DLQ
                             reason: MaxDeliveryCountExceeded

Status não suportado -> rollback do Inbox -> DLQ manual:
                       UnsupportedBusinessStatus
```

O status não suportado ocorre depois de entender a mensagem, mas antes do sucesso; por isso o Inbox sofre rollback antes do dead-letter. Em falha de settlement, o banco pode já estar `Completed`, o broker pode redeliver e a idempotência pode pular o negócio. Se o transporte continuar falhando, aplica-se `MaxDeliveryCountExceeded`.

## Mapa de falha, ação e resultado

| Tipo de falha | Exemplos no CloudOrders | Ação da aplicação | Resultado esperado |
| --- | --- | --- | --- |
| Contrato permanente | subject ou content type inesperado, body vazio, JSON inválido, `MessageId` ausente | Dead-letter manual imediato | DLQ com `MessageContractViolation`, sem Inbox e sem completion |
| Regra de negócio permanente | `OrderCreated` válido com status diferente de `Pending` | Rollback da tentativa do Inbox e dead-letter manual | DLQ com `UnsupportedBusinessStatus` |
| Processamento transitório | callback do Payment/Inventory/Notification falha temporariamente | Propaga exceção; não completa e não faz DLQ manual | Service Bus redeliverá |
| Persistência transitória | PostgreSQL ou `IProcessedMessageStore` indisponível | Propaga exceção; não completa | Redelivery; depois da quinta tentativa, DLQ do broker |
| Settlement/transport | `CompleteMessageAsync` falha, lock expira, receiver perde conexão | Propaga falha de transporte | Pode haver redelivery; Inbox pode evitar novo efeito |
| Falha permanente repetida não reconhecida | poison message que sempre quebra o processamento | Continua unsettled conforme política atual | DLQ eventual com `MaxDeliveryCountExceeded` |

## Por que `MaxDeliveryCount = 5`

O limite é configurado nas entidades `payment`, `inventory` e `notification` quando o namespace é provisionado pelo setup Azure CLI documentado. Ele não é uma opção de `ServiceBusProcessorOptions`, não é imposto pelo código local e não altera a configuração do `messaging-probe`, que permanece separada.

Cinco foi um limite pedagógico e operacional: permite observar recuperação temporária sem prender indefinidamente uma mensagem quebrada. Não é universal; uma integração real deve relacioná-lo ao tempo de recuperação, lock, custo do provider e urgência do negócio.

Quando uma falha transitória deixa a mensagem unsettled, o broker conta as entregas. Se o PostgreSQL volta antes do quinto delivery, uma nova tentativa pode processar, gravar o Inbox e completar. Se continua indisponível, o Service Bus termina o ciclo na DLQ com `MaxDeliveryCountExceeded`. O broker passa a ser o mecanismo de bounded retry, e não o worker.

## Por que não Polly, loop local ou abandon explícito

Um retry local exige decidir tentativas dentro do lock, backoff, contagem, logs e comportamento em caso de crash. Também pode prolongar o lock e atrasar outras mensagens da subscription.

O código não chama `AbandonMessageAsync` para simular um retry manual. Lançar a exceção e deixar a entrega unsettled já expressa ao processor que a mensagem não foi concluída. A camada Azure e o broker cuidam da redelivery e do limite. Essa decisão reduz a política customizada, mantém os workers simétricos e evita introduzir Polly antes de existir um requisito de backoff ou de rate limiting.

Polly ou retry explícito podem ser úteis para um provider real, mas devem ser desenhados com idempotency key, lock renewal, custo e efeitos parciais. Não são automaticamente corretos no handler atual.

## DLQ útil, segura e operável

Dead-lettering não deve despejar o payload completo no broker. A descrição deve permitir investigação sem expor dados de pedido, credenciais ou connection strings.

`MessageDeadLetterDetailsFactory` inclui na descrição o consumer lógico, o native `MessageId`, o tipo de exceção e uma mensagem concisa, por exemplo:

```text
Consumer 'Payment' rejected message 'm-123' with JsonException: ...
```

O factory escolhe a razão de forma determinística e limita a descrição a 4.096 caracteres. Os processors não incluem o body da mensagem. O README também registra que body, customer payload, credentials, connection strings e parâmetros SQL não são anexados a logs ou telemetry.

Essa separação é útil para o diagnóstico: a razão informa a classe operacional; a descrição explica o contexto mínimo. O `MessageId` permite correlacionar com logs e métricas, mas não deve ser usado como desculpa para registrar o payload inteiro.

## Poison messages

Uma poison message sempre provoca a mesma falha. JSON inválido é poison para o contrato atual e vai imediatamente à DLQ; status não suportado também não ganhará suporte por repetição.

Uma indisponibilidade temporária do PostgreSQL não deve ser chamada de poison message cedo demais. O smoke test do README demonstra a diferença: interromper o banco depois de os workers estarem rodando, publicar um `Pending` válido e restaurar o banco antes do quinto delivery deve permitir que a mensagem termine. Mantendo o banco indisponível, o mesmo cenário deve alcançar a DLQ do broker com `MaxDeliveryCountExceeded`.

O diagnóstico deve perguntar: o erro é determinístico? É compartilhado por todas as mensagens? Provider ou banco está indisponível? O `DeliveryCount` aumenta sem mudança? Falha permanente reconhecida merece DLQ; erro temporário ou incerto merece redelivery.

## Smoke test como evidência concreta

O README propõe validar com os três workers rodando e o Service Bus Explorer:

1. Enviar para `order-events` uma mensagem com subject `CloudOrders.Orders.OrderCreated`, content type JSON e body malformado, como `not-json`. Cada subscription de negócio deve produzir uma entrada DLQ com `MessageContractViolation`; a descrição deve nomear o consumer e o `MessageId`, sem body.
2. Repetir com JSON válido de `OrderCreated`, mas status `Cancelled`. O evento passa pela desserialização, falha na regra de negócio e deve aparecer com `UnsupportedBusinessStatus`.
3. Parar temporariamente o PostgreSQL, publicar um `Pending` válido e restaurar o banco antes do quinto delivery. O esperado é redelivery e conclusão posterior sem repetir o efeito protegido pelo Inbox.
4. Em uma execução controlada separada, manter o PostgreSQL indisponível e confirmar a entrada `MaxDeliveryCountExceeded` na DLQ da subscription afetada.

Esse roteiro é smoke test de desenvolvimento, não teste automatizado contra Azure. Unit tests usam delegates sem credenciais; integration tests usam PostgreSQL Testcontainers. O smoke test acrescenta evidência de contagem, DLQ e settlement do broker.

## Settlement e idempotência trabalham juntos

O worker mantém `AutoCompleteMessages = false` e PeekLock. Em um sucesso, o Inbox grava `Completed` antes de `CompleteMessageAsync`. Se completion falhar, a mensagem pode voltar, mas a nova entrega encontra a combinação `(ConsumerName, MessageId)` concluída, pula o processador e tenta completar novamente.

Esse mecanismo também protege uma falha transitória que ocorre depois de uma operação já ter sido commitada no Inbox. Ele evita reexecutar a simulação, mas não transforma qualquer provider externo em exactly-once. O atual Payment, Inventory e Notification não possuem efeitos externos; um futuro gateway ou sistema de estoque precisará de idempotency keys, reconciliação e tratamento de timeout ambíguo.

## O que continua adiado

O change não adicionou Polly, backoff customizado, replay de DLQ, provider real, Outbox ou deployment. A DLQ é destino operacional, não recuperação completa: faltaria decidir inspeção, correção, reencaminhamento seguro e auditoria.

Também não há política para distinguir timeout, rate limit, autenticação e indisponibilidade regional. Essas decisões ficam para incrementos futuros. A fronteira atual é clara: contrato permanente sai rápido; falha transitória volta ao broker; falha repetida chega à DLQ.

## Mental model

Imagine uma triagem postal. Se o endereço está ilegível, a carta vai imediatamente para a caixa de exceções. Se o destinatário está temporariamente sem atendimento, o carteiro tenta novamente em outra rodada. Se o atendimento nunca volta dentro do limite de cinco visitas, a carta vai para a caixa de exceções do broker.

O worker não fica fazendo cinco visitas dentro da mesma chamada. Ele classifica o que sabe, confirma somente o sucesso e devolve a incerteza ao Service Bus. O Inbox funciona como o carimbo local que impede repetir o trabalho já commitado quando a confirmação da entrega se perde.

## Glossário

- **Retry local:** nova execução feita pelo próprio handler antes de devolver a mensagem.
- **Redelivery:** nova entrega solicitada pelo broker após uma tentativa unsettled.
- **Dead Letter Queue (DLQ):** subfila de mensagens retiradas do fluxo normal para diagnóstico ou recuperação.
- **Permanent failure:** falha conhecida que não será corrigida por repetição da mesma mensagem.
- **Transient failure:** falha potencialmente recuperável em uma próxima tentativa.
- **Poison message:** mensagem que falha repetidamente por sua forma ou conteúdo.
- **Settlement:** confirmação ou mudança de estado da mensagem no broker.
- **PeekLock:** recebimento com lock temporário antes de completion.
- **MaxDeliveryCount:** limite de deliveries antes do dead-letter automático do broker.
- **Abandon:** settlement que libera a mensagem para nova entrega; o CloudOrders não o chama explicitamente.
- **Backoff:** espera crescente entre tentativas.
- **Inbox:** estado durável do consumer para deduplicar processamento já concluído.

## Perguntas de revisão

1. **Qual a diferença entre retry local e redelivery?**  
   Retry local acontece dentro do handler; redelivery é uma nova entrega feita pelo broker depois que a tentativa anterior não foi completada.

2. **Por que um JSON inválido vai direto para a DLQ?**  
   Porque repetir a mesma mensagem não corrige seu contrato. O worker classifica `ContractViolation`, usa a razão `MessageContractViolation` e não cria Inbox nem completion.

3. **O que acontece com uma falha de PostgreSQL?**  
   Ela permanece unsettled e propaga. O Service Bus redeliverá; se o banco voltar, a mensagem pode completar; caso contrário, após cinco deliveries, vai para DLQ com `MaxDeliveryCountExceeded`.

4. **Por que `MaxDeliveryCount = 5` não aparece em `ServiceBusProcessorOptions`?**  
   Porque é uma propriedade da entidade subscription no broker, configurada no setup do Service Bus, e não uma opção local do processor.

5. **Como o Inbox ajuda quando completion falha?**  
   A próxima entrega encontra o par consumer/`MessageId` como `Completed`, pula o negócio e tenta somente o settlement novamente.

## Cenários de entrevista

1. **“Um cliente pede retry imediato três vezes dentro do worker para reduzir tempo de recuperação.”**  
   Raciocínio sugerido: primeiro classifique a falha e considere lock, throughput e contagem de deliveries. Para a política atual, deixe a exceção propagar e use redelivery do broker. Só adicione retry local se houver requisito claro de backoff/provider e uma estratégia de idempotência.

2. **“Uma mensagem `Cancelled` está ocupando a subscription até atingir cinco tentativas.”**  
   Raciocínio sugerido: isso indica classificação incorreta ou worker antigo. O JSON válido com status não suportado deve lançar a falha permanente, fazer rollback do Inbox e ir imediatamente para `UnsupportedBusinessStatus`, não consumir o limite de redelivery.

3. **“Uma DLQ contém descrições com o JSON completo do pedido para facilitar suporte.”**  
   Raciocínio sugerido: substituir por consumer, `MessageId`, tipo de exceção e detalhe conciso. Body pode conter dados sensíveis e tornar a DLQ um novo vazamento. A investigação deve usar correlação com logs/telemetry controlados.

## Key takeaways

- Retry local, redelivery e dead-lettering são mecanismos diferentes.
- O CloudOrders deixa falhas transitórias unsettled e usa o broker como bounded retry.
- Contratos inválidos recebem `MessageContractViolation` imediatamente.
- Status de negócio não suportado recebe `UnsupportedBusinessStatus` após rollback do Inbox.
- `MaxDeliveryCount = 5` limita falhas transitórias persistentes nas subscriptions de negócio.
- Não há Polly, loop local ou `AbandonMessageAsync` explícito nesta etapa.
- Settlement, lock e receiver failures continuam sendo falhas de transporte.
- Descrições de DLQ são úteis e limitadas, mas não carregam payloads ou segredos.
- O Inbox torna redelivery segura para o processamento já commitado, sem prometer exactly-once externo.
- Replay de DLQ e políticas avançadas continuam sendo evolução futura.
