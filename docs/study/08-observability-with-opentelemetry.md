# Observabilidade com OpenTelemetry no CloudOrders

Observabilidade é a capacidade de entender o que aconteceu a partir dos sinais que um sistema produz. No CloudOrders, isso significa descobrir se o pedido foi persistido e publicado, qual worker recebeu a cópia e se uma falha PostgreSQL ocorreu na conexão ou durante uma query.

O change arquivado `add-observability` introduziu a base sem dashboards, alertas ou deployment Azure. O estado atual acrescenta instrumentação do Transactional Outbox; essa evolução é distinguida quando relevante.

## Os três pilares

**Logs** registram acontecimentos discretos, como persistência, publicação, falha, duplicate-skip e settlement. O console usa JSON e scopes estruturados com `Service`, `Operation`, `OrderId`, `MessageId`, `CorrelationId`, `TraceId`, `SpanId`, `Consumer`, `Outcome` e `DeliveryCount` quando disponíveis.

**Traces** mostram uma operação como árvore temporal. No CloudOrders atual, a requisição HTTP contém a persistência de Order + Outbox; a publicação ocorre depois no dispatcher e o processamento ocorre nos workers. O contexto W3C relaciona essas activities em momentos e processos distintos; `TraceId` liga a árvore e `SpanId` identifica cada trabalho.

**Metrics** agregam comportamento: pedidos persistidos, mensagens processadas, duração, redeliveries e settlements. São adequadas para tendência, taxa e alerta, não para a identidade individual de cada pedido.

Um log não substitui causalidade de trace; um trace não mostra facilmente a taxa de erro da população. Os três sinais se complementam.

## Conceitos de OpenTelemetry aplicados

OpenTelemetry fornece APIs e SDKs padronizados para produzir e exportar telemetria.

- **Resource:** descreve quem produz o sinal. `ConfigureResource(...AddService(serviceName))` define o `service.name`.
- **ActivitySource:** fábrica provider-neutral de activities .NET; o source do projeto é `CloudOrders`.
- **Activity/Span:** operação com duração, parent, status, tags e contexto. O código cria activities de order, Outbox e messaging.
- **Meter:** fábrica de instrumentos; o meter também se chama `CloudOrders`.
- **Instrument:** medição agregada, como `Counter<long>`, `Histogram<double>` ou `ObservableGauge<long>`.
- **Exporter:** destino dos sinais. O projeto oferece Console, OTLP e `None`.

O source `CloudOrders` não é `service.name`: o primeiro é a origem das activities; o segundo identifica o processo no backend.

## Services e boundaries atuais

`CloudOrdersTelemetry` centraliza os nomes estáveis:

| Processo | `service.name` |
| --- | --- |
| API | `cloudorders-api` |
| Payment Worker | `cloudorders-payment-worker` |
| Inventory Worker | `cloudorders-inventory-worker` |
| Notification Worker | `cloudorders-notification-worker` |

`CloudOrders.Api` chama `AddCloudOrdersObservability` com `ApiServiceName` e `includeAspNetCoreInstrumentation: true`. Os três workers usam a mesma extensão com nomes próprios, sem ASP.NET Core instrumentation por serem Generic Hosts. O probe foi excluído deliberadamente.

## Identidade de negócio versus identidade técnica

O CloudOrders usa identificadores relacionados, mas não equivalentes:

- **`OrderId`:** identifica o pedido no domínio. Ajuda a responder “qual negócio está sendo tratado?” e é adicionado como tag/log property quando o pedido é conhecido.
- **`MessageId`:** identifica tecnicamente a mensagem lógica nativa do Service Bus. Ele permanece igual nas cópias de fan-out e em redeliveries; `DeliveryCount` identifica a tentativa de entrega.
- **`CorrelationId`:** liga a mensagem ao contexto de negócio. No fluxo de `OrderCreated`, o API usa o `OrderId` como `CorrelationId` nativo.
- **`TraceId`:** identifica tecnicamente uma distributed trace W3C. Pode atravessar processos e spans, mas não é o identificador do pedido nem deve ser tratado como dado de domínio.

Eles aparecem juntos porque respondem perguntas diferentes: `OrderId` aponta para a entidade, `MessageId` para a mensagem lógica, `DeliveryCount` para a tentativa de entrega, `CorrelationId` para a relação do evento e `TraceId` para a árvore técnica. Confundi-los causa diagnóstico errado.

## W3C trace context não é payload de negócio

O W3C Trace Context propaga `traceparent` e `tracestate`, informando parent, trace ID, span ID e sampling flags. Ele reconstrói causalidade; não descreve `OrderCreated`.

O contrato contém `orderId`, `customerId`, `status` e `createdAtUtc`. Hoje, a API captura o contexto na activity e o guarda como metadata do Outbox; o dispatcher o usa como parent válido de `cloudorders.outbox.dispatch`. O JSON permanece inalterado.

Assim, o contrato não depende da biblioteca de tracing nem vaza detalhes técnicos. Como o Outbox atrasa a publicação, o contexto é persistido fora do payload para preservar continuidade.

## O que é instrumentado

A extensão `AddCloudOrdersObservability` registra:

- **ASP.NET Core:** somente na API, para HTTP;
- **Npgsql:** tracing e metrics PostgreSQL;
- **EF Core:** o meter `Microsoft.EntityFrameworkCore`;
- **runtime .NET:** métricas de execução;
- **Service Bus SDK:** o source exato `Azure.Messaging.ServiceBus.Message`;
- **CloudOrders:** activities, logs e instrumentos customizados.

Essas referências ficam concentradas em `CloudOrders.Infrastructure` e são versionadas pelo `Directory.Packages.props`: OpenTelemetry 1.18.0, `Npgsql.OpenTelemetry` 10.0.3 e `Azure.Messaging.ServiceBus` 7.20.2. A aplicação e os workers recebem essa capacidade pela referência à Infrastructure.

No pedido, `CreateOrderHandler` cria `cloudorders.order.create` e, no estado atual, `cloudorders.outbox.persist`. Adiciona `OrderId`, `MessageId` e `CorrelationId`, persiste Order/Outbox e marca `enqueued`. O dispatcher cria `cloudorders.outbox.dispatch` e marca `published` após a publicação.

Nos workers, os três `MessageProcessor` criam `cloudorders.messaging.process` com consumer, MessageId, CorrelationId e DeliveryCount. O span cobre validação, Inbox, simulação e settlement; falhas são `Error` com `error.type`, e sucessos são `Ok` com outcome como `processed` ou `duplicate`.

## A particularidade do Service Bus ActivitySource

O repositório resolve `Azure.Messaging.ServiceBus` 7.20.2. A documentação do package descreve seu ActivitySource como experimental e controlado por feature flag. Por isso, não há `AppContext` switch.

Para obter spans de transporte de send, process e settlement, a variável deve existir antes do startup:

```text
AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE=true
```

Sem essa opt-in, activities e métricas customizadas continuam disponíveis, mas os spans internos do SDK não são esperados. A ausência deles não prova ausência de publicação.

## Métricas e cardinalidade

O meter registra pedidos persistidos/publicados, Outbox, tentativas/pending, mensagens processadas, duração, settlements, dead letters, redeliveries e erros. As dimensões são low-cardinality: `consumer`, `outcome`, `settlement`, `reason` e `error_source`.

`OrderId`, `MessageId`, CorrelationId e TraceId servem a logs/traces, mas não a metric dimensions. Cada ID criaria uma série temporal; em escala, isso eleva custo e dificuldade de consulta. Métrica responde “quantas mensagens Payment?”; trace responde “o que ocorreu com `m-123`?”.

Os testes refletem a regra: o teste focado de Payment captura measurements e verifica que nenhuma tag contém `id`, enquanto listeners de Activity verificam identificadores. Trace tem precisão individual; métrica permanece agregada.

## Logs estruturados e limites de dados

O console usa formatter JSON, scopes e timestamps UTC. `CloudOrdersTelemetry` cria scopes para `CreateOrder`, `ProcessOrderCreated`, `DispatchOutboxMessage` e `ServiceBusProcessor`, com propriedades estruturadas.

Logs e activity tags não carregam message bodies, customer payloads, credenciais, connection strings ou SQL parameter values. Exception messages não são atributos de telemetry; métricas usam tipos, outcomes e razões controladas. A DLQ fornece consumer, MessageId, tipo e detalhe conciso, sem body.

`OrderId`, `MessageId`, `CorrelationId`, `TraceId`, `SpanId`, `DeliveryCount`, `Outcome` e `FailureType` normalmente bastam para localizar a operação. Observabilidade também é política de privacidade.

## Fluxo distribuído de uma trace

```text
Cliente
  |
  | HTTP POST /orders
  v
API: cloudorders-api
  |-- span cloudorders.order.create
  |     |-- ASP.NET Core HTTP span
  |     |-- span cloudorders.outbox.persist
  |     |     |-- EF Core / Npgsql spans
  |     |     '-- COMMIT: orders + outbox_messages
  |
  '-- dispatcher (ainda API)
        |-- span cloudorders.outbox.dispatch
        |     '-- Service Bus SDK send span* 
        v
  Topic order-events / subscriptions
        |
        +--> Payment Worker
        |      '-- cloudorders.messaging.process
        |            '-- EF Core / Npgsql: processed_messages
        |                  '-- Service Bus settlement span*
        |
        +--> Inventory Worker
        |      '-- cloudorders.messaging.process
        |            '-- EF Core / Npgsql: processed_messages
        |
        '--> Notification Worker
               '-- cloudorders.messaging.process
                     '-- EF Core / Npgsql: processed_messages

* span do SDK esperado quando a feature flag do Service Bus está ativa
```

O desenho mostra que a trace não é uma única chamada HTTP longa: API, dispatcher e workers continuam em momentos distintos. O contexto W3C do Outbox preserva a relação sem entrar no JSON do evento.

## Como interpretar uma trace de worker saudável

Em um pedido normal, espere activity da API com `OrderId`, `MessageId` e `CorrelationId`, persistência concluída, dispatch publicado e uma activity de processamento para cada subscription. Cada worker tem `service.name` próprio.

O span do worker termina com outcome de processamento/duplicate e status `Ok` quando trabalho e settlement têm sucesso. Um span Npgsql/EF demonstra acesso ao Inbox; um span SDK depende da opt-in. A métrica usa tags estáveis, sem ID do pedido.

Uma trace saudável não exige que os workers terminem juntos: fan-out é independente e cada caminho tem seu settlement.

## Como ler uma falha de PostgreSQL

Uma falha de autenticação e uma tabela ausente aparecem em pontos diferentes:

- **Autenticação/conexão:** Npgsql falha ao abrir/autenticar. O span do banco falha antes de SQL útil; confira connection string, servidor, usuário e segredo sem copiá-los para logs.
- **Tabela/migration ausente:** a conexão funciona, mas uma query/insert falha porque `orders`, `processed_messages` ou `outbox_messages` não existe, ou o schema divergiu. Verifique migrations, startup project, ambiente e banco usado.

A diferença evita o diagnóstico genérico “Postgres caiu”: migration não corrige senha, e trocar senha não cria tabela. O trace localiza a etapa da falha.

## Runtime metrics não significam mensagem processada

Ao usar Console exporter, um dump pode mostrar métricas de runtime, EF Core, Npgsql e CloudOrders. GC, threads, memória ou instrumento registrado provam apenas que o processo emite sinais, não que `OrderCreated` foi processado.

Para afirmar processamento, combine activity `cloudorders.messaging.process`, logs com consumer/identifiers, `cloudorders.messaging.processed` com outcome e evidência de settlement. `processor.errors` indica falha de infraestrutura, não sucesso; `processed` também deve ser lida com seu outcome.

## Console local, OTLP e Azure Monitor

Development configura `Observability:Exporter = Console`, útil para ver traces e métricas no terminal sem collector. É ruidoso e serve à inspeção imediata, não à consulta histórica.

`Observability:Exporter = Otlp` usa `OTEL_EXPORTER_OTLP_*` para apontar a um collector. O Compose não inclui collector, dashboard ou alerting; OTLP exige um destino configurado.

Fora de Development, o default é `None`; testes não dependem de OTLP, Azure ou backend. Uma futura integração com Azure Monitor pode trocar apenas a borda de exportação.

## Sampling, volume e custo

Tracing gera spans por operação; métricas e logs têm volumes diferentes. Em produção, sampling pode manter todas as traces de erro e uma fração das saudáveis. Isso não significa apagar métricas críticas ou logs de falha; o repositório não implementa uma política própria de sampling.

Para aprendizagem, Console e pequenas cargas são baratos. Em escala, cada span HTTP, SQL, Service Bus e redelivery custa volume. Prioridades: metrics low-cardinality, payloads limitados, retenção definida, sampling de sucesso e contexto suficiente para incidentes.

## How to read a CloudOrders incident from telemetry

1. **Comece pelo service e pelo tempo.** Identifique `service.name`, ambiente e janela; API, dispatcher e workers são processos distintos.
2. **Escolha um identificador.** Use `OrderId` para o pedido, `MessageId` para a mensagem lógica, `DeliveryCount` para a entrega, `CorrelationId` para a relação do evento e `TraceId`/`SpanId` para navegar na árvore.
3. **Siga os spans.** Confira `cloudorders.order.create`, persistência/Outbox, `cloudorders.outbox.dispatch` e `cloudorders.messaging.process` no worker.
4. **Localize a primeira falha.** Npgsql/EF aponta para banco; activity de aplicação aponta para processamento; ausência de span SDK pode ser feature flag desativada.
5. **Compare logs e métricas.** Confirme `Outcome`, `DeliveryCount`, consumer, settlement, redelivery e reason; runtime dump não prova negócio.
6. **Separe roteamento de processamento.** Sem span de worker, investigue topic, filtro, Service Bus e configuração; com falha Npgsql, investigue o PostgreSQL daquele processo.
7. **Proteja dados.** Não copie body, customer payload, connection string ou segredo; use IDs e contexto permitido.

## Troubleshooting checklist

- Confirmar `Observability:Exporter`: Console em Development, OTLP somente com collector e `None` quando não há backend.
- Conferir se `OTEL_EXPORTER_OTLP_*` aponta para um destino acessível e se o exporter foi selecionado antes do startup.
- Verificar o `service.name` esperado: `cloudorders-api`, `cloudorders-payment-worker`, `cloudorders-inventory-worker` ou `cloudorders-notification-worker`.
- Se faltam spans de Service Bus, habilitar a variável `AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE=true` antes de iniciar; não adicionar `AppContext` por conta própria.
- Se existe span de aplicação, mas não há evidência de mensagem, conferir o caminho do Outbox, publicação, topic, filtros e subscription.
- Diferenciar `cloudorders.messaging.processed`, redelivery, settlement, dead-letter e processor error; são instrumentos diferentes.
- Em erro de banco, distinguir autenticação de tabela/migration ausente e confirmar que o processo usa o banco correto.
- Procurar IDs em logs/traces, não em metric dimensions; investigar cardinalidade antes de adicionar uma nova tag.
- Lembrar que o probe worker foi excluído da instrumentação do change arquivado.
- Reproduzir com os testes focados: listeners de `ActivitySource`/`Meter` validam tags, outcomes e cardinalidade sem backend externo.

## Mental model

Pense no CloudOrders como uma operação acompanhada por três instrumentos. Logs são o diário: “algo aconteceu”. Traces são o mapa: “esta etapa veio daquela outra”. Métricas são o painel: “com que frequência e por quanto tempo isso acontece?”.

`OrderId` é a etiqueta do pedido; `MessageId` é o identificador do envelope; `CorrelationId` liga o evento ao negócio; `TraceId` é o fio técnico que atravessa os processos. O Resource coloca cada observação no serviço certo. O exporter decide para onde os sinais vão, mas não muda o significado deles.

## Glossário

- **Observabilidade:** capacidade de inferir o estado interno de um sistema por seus sinais externos.
- **Resource:** atributos descritivos do processo que produz telemetria.
- **`service.name`:** identidade estável de um serviço no backend de observabilidade.
- **ActivitySource:** origem/fábrica de activities da aplicação .NET.
- **Activity/Span:** unidade temporal de uma operação com contexto, tags e status.
- **Meter:** origem/fábrica dos instrumentos métricos.
- **Counter:** instrumento monotônico de contagem.
- **Histogram:** instrumento para distribuição de valores, como duração.
- **Gauge:** observação de um valor corrente, como pending count.
- **Exporter:** componente que envia logs, traces ou métricas a um destino.
- **OTLP:** protocolo OpenTelemetry para transportar telemetria a um collector/backend.
- **W3C Trace Context:** padrão de propagação de `traceparent`/`tracestate`.
- **Cardinalidade:** quantidade de valores distintos em uma dimensão; alta cardinalidade encarece métricas.
- **Sampling:** seleção de quais traces serão mantidas/exportadas.
- **Outcome:** resultado controlado da operação, como `processed`, `duplicate` ou `failed`.
- **Service Bus ActivitySource:** source do SDK para spans de transporte, condicionado pela feature flag atual.

## Perguntas de revisão

1. **Qual é a diferença entre um log, um trace e uma métrica?**  
   Log descreve um acontecimento; trace mostra a sequência temporal e causal de spans; métrica agrega contagens, duração ou estado para tendências e alertas.

2. **Por que `OrderId` não deve ser dimensão de uma métrica?**  
   Porque cada pedido cria uma série temporal diferente. Isso gera alta cardinalidade; o ID deve ficar em logs e traces, enquanto a métrica usa `consumer` e `outcome` estáveis.

3. **O que `service.name` identifica no CloudOrders?**  
   O processo produtor do sinal: API, Payment Worker, Inventory Worker ou Notification Worker, cada um com um nome constante.

4. **Por que uma trace pode ter spans da API e do worker sem a API esperar o worker?**  
   Porque o Outbox e o Service Bus fazem a comunicação assíncrona. O contexto W3C relaciona as operações, mas não transforma o fluxo em uma transação HTTP síncrona.

5. **O que significa não encontrar um span interno do Service Bus?**  
   Pode significar que `AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE=true` não estava definida antes do startup. As activities customizadas do CloudOrders ainda podem existir.

## Cenários de entrevista

1. **“A API responde, mas Payment não aparece na trace.”**  
   Raciocínio sugerido: verificar se a trace mostra persistência do pedido, Outbox pending/dispatch, publicação, topic e filtro da subscription. Depois conferir o `service.name` do worker e a feature flag do SDK. Ausência do span de transporte não prova ausência da mensagem; pode faltar apenas a instrumentação experimental.

2. **“O dashboard mostra aumento de CPU e runtime metrics, mas ninguém sabe se os pedidos foram processados.”**  
   Raciocínio sugerido: runtime signals mostram saúde do processo, não sucesso de negócio. Procurar `cloudorders.messaging.processed`, outcome, logs com MessageId/OrderId e traces de processamento/settlement. Se necessário, consultar a métrica de redelivery ou dead letter.

3. **“O worker falha no banco; como diferenciar senha incorreta de migration ausente?”**  
   Raciocínio sugerido: observar o span Npgsql/EF e a etapa da falha. Falha de autenticação ocorre ao abrir a conexão; tabela ausente ocorre depois, durante query/insert. Corrigir a causa correspondente e evitar registrar credenciais ou payloads no diagnóstico.

## Key takeaways

- Logs, traces e métricas respondem perguntas diferentes e devem ser correlacionados.
- `CloudOrdersTelemetry` centraliza `ActivitySource`, `Meter`, nomes de serviço, tags, scopes e instrumentos.
- `OrderId`, `MessageId` e `CorrelationId` são identidades úteis de negócio/integração; `TraceId` é identidade técnica W3C.
- W3C trace context é metadata técnica e permanece fora do JSON de `OrderCreated`.
- A API recebe ASP.NET Core instrumentation; todos os hosts registram Npgsql/EF/runtime e a instrumentação customizada.
- Spans do Service Bus SDK dependem da feature flag experimental da versão 7.20.2 resolvida pelo repositório.
- Metrics usam dimensões low-cardinality; IDs pertencem a logs e traces, não a séries métricas.
- Console é adequado para aprendizagem local; OTLP e futura integração Azure Monitor servem à centralização.
- Um erro de autenticação PostgreSQL acontece antes de SQL; uma tabela ausente aponta para migration/schema após a conexão.
- Runtime metric dumps não provam que uma mensagem de negócio foi processada.
- Sampling, retenção e volume precisam equilibrar diagnóstico, privacidade e custo.
