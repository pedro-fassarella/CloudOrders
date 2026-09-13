# Walking skeleton e arquitetura do CloudOrders

Um projeto distribuído raramente começa distribuído. Antes de adicionar filas, workers, retries e dashboards, é preciso provar que uma requisição atravessa o sistema e chega a uma fronteira real de persistência. O CloudOrders começou com um walking skeleton pequeno, executável e verificável.

Este capítulo usa o repositório como exemplo e separa o *estado inicial* de `bootstrap-cloudorders-walking-skeleton` do *estado atual*, que já inclui mensageria, workers, idempotência, observabilidade e Outbox.

## O que é um walking skeleton

Um walking skeleton é o menor caminho vertical que atravessa as partes essenciais de uma aplicação. Ele demonstra que entrada HTTP, caso de uso, domínio, infraestrutura e resposta conseguem trabalhar juntos, sem representar todas as funcionalidades do produto.

No CloudOrders, o esqueleto inicial tinha quatro projetos de produção — `CloudOrders.Api`, `CloudOrders.Application`, `CloudOrders.Domain` e `CloudOrders.Infrastructure` — além de `CloudOrders.UnitTests` e `CloudOrders.IntegrationTests`. A fatia funcional era propositalmente estreita: `POST /orders` recebia um `customerId`, criava um pedido mínimo com status `Pending`, persistia-o no PostgreSQL e retornava `201 Created`. `GET /health` confirmava que o processo estava vivo sem depender de um serviço externo.

Começar por essa fatia resolveu riscos importantes cedo: SDK .NET 10, Dependency Injection, formato HTTP, modelagem de `Order`, conexão com o banco e testes. Também tornou visível o custo de rodar o sistema localmente.

## O fluxo original, de ponta a ponta

No estado inicial, o fluxo de criação era síncrono e não publicava evento. O endpoint validava a entrada; o caso de uso criava a entidade; a porta de persistência era implementada por EF Core/Npgsql; e somente depois da gravação a API produzia a resposta.

```text
Cliente HTTP
    |
    | POST /orders { customerId }
    v
CloudOrders.Api
    | binding + validação + chamada do handler
    v
CreateOrderHandler (Application)
    | Order.Create(...)
    v
Order (Domain)
    |
    | IOrderStore.AddAsync(...)
    v
EfOrderStore (Infrastructure)
    | CloudOrdersDbContext.SaveChangesAsync()
    v
PostgreSQL
    |
    | pedido persistido
    v
HTTP 201 Created + OrderResponse
```

O contrato inicial exigia `customerId` não vazio, um novo GUID, o horário UTC atual e o status `Pending`. Entrada inválida retornava `400` com `ProblemDetails` e não tentava persistir. O endpoint não retornava `202 Accepted`, não processava pagamento e não conhecia inventário.

Hoje, a requisição preserva o contrato público, mas o caso de uso prepara também `OrderCreated`. `EfOrderStore` grava pedido e mensagem Outbox na mesma transação PostgreSQL; um dispatcher publica depois do commit, sem a API esperar o Azure Service Bus. Isso é evolução do esqueleto, não parte da primeira versão.

## As fronteiras da solution

```text
+---------------------------------------------------------------+
| CloudOrders.Api                                               |
| HTTP, binding, ProblemDetails, health, configuração e DI      |
|                       composition root                        |
+-------------------------------+-------------------------------+
                                | usa
                                v
+---------------------------------------------------------------+
| CloudOrders.Application                                        |
| casos de uso, comandos, portas e contratos necessários        |
+-------------------------------+-------------------------------+
                                | depende de
                                v
+---------------------------------------------------------------+
| CloudOrders.Domain                                             |
| Order, OrderStatus e invariantes do domínio                    |
| sem ASP.NET Core, EF Core, PostgreSQL ou Azure                 |
+---------------------------------------------------------------+

CloudOrders.Infrastructure ------------------------------------+
EF Core, Npgsql, DbContext, migrations, Service Bus, Outbox,   |
observabilidade e implementações das portas                    |
           ^                                                   |
           | implementa portas da Application; conhece Domain  |
           +---------------------------------------------------+

Workers atuais: hosts independentes que reutilizam Application
e Infrastructure para consumir mensagens e executar seu slice.
```

O diagrama mostra duas ideias ao mesmo tempo. Primeiro, `Domain` permanece no centro e é independente. Segundo, `Infrastructure` aponta conceitualmente para dentro: ela conhece as portas da `Application` para implementá-las, mas a regra de negócio não conhece Npgsql ou Azure. `Api` é a composition root: registra `AddApplication()`, `AddInfrastructure(...)`, publishers, dispatcher e observabilidade.

### Api

`CloudOrders.Api/Program.cs` é a borda HTTP e a composition root. Ele mapeia `/orders` e `/health`, valida a entrada e transforma o resultado em `OrderResponse`; não instancia `CloudOrdersDbContext` nem chama SQL. No estado atual, habilita o Outbox Dispatcher e, em Development, expõe o probe de mensageria. O `/health` inicial era independente do banco; depois de `add-orders-persistence`, `AddDbContextCheck` passou a verificar o PostgreSQL atual.

### Application

`CloudOrders.Application/Orders/CreateOrder.cs` contém `CreateOrderCommand`, `CreateOrderHandler`, `CreatedOrder` e a porta `IOrderStore`. O handler coordena a ação: pede ao domínio uma `Order`, produz o contrato `OrderCreated` e delega a persistência. As interfaces em `Messaging`, como `IMessagePublisher`, `IOutboxMessageStore` e `IProcessedMessageStore`, expressam necessidades da aplicação sem importar tipos do Azure.

`OrderCreated` está em `Application/Messaging`, e não em um projeto `Contracts`. O ADR 0002 adiou esse assembly até existir necessidade clara de uma fronteira compartilhada independente: compartilhar um tipo não obriga criar um projeto.

### Domain

`CloudOrders.Domain/Orders/Order.cs` guarda a regra mínima de criação. `Order.Create` rejeita ID vazio e `customerId` ausente ou em branco, normaliza o identificador e inicia a entidade como `Pending`. O domínio não recebe logger, banco, publisher ou configuração. O horário chega como dado do caso de uso, obtido por `TimeProvider`, o que mantém a decisão testável.

### Infrastructure

`CloudOrders.Infrastructure` contém os adaptadores. No início, isso significava `CloudOrdersDbContext`, o mapeamento de `orders`, `EfOrderStore` e `InitialCreate`. Hoje o mesmo projeto abriga `processed_messages`, `outbox_messages`, publisher Azure Service Bus, dispatcher, Npgsql/OpenTelemetry e extensões de DI.

Os workers atuais são hosts separados: `Payment.Worker`, `Inventory.Worker` e `Notification.Worker` simulam `Pending`; `Messaging.Worker` mantém o probe. Assim, código de worker não entra na API.

## Direção das dependências e valor das fronteiras

Uma fronteira limita o que uma mudança pode contaminar: alterar PostgreSQL não deveria alterar `Order`; trocar transporte não deveria reescrever `CreateOrderHandler`; testar domínio não deveria iniciar Docker.

No repositório, `Application` referencia `Domain`; `Infrastructure` referencia ambos; `Api` referencia `Application` e `Infrastructure` para compor o grafo. `Domain` não referencia nenhum projeto. A dependência física dos projetos torna a regra auditável: não é apenas uma convenção de namespaces.

As portas são pequenas de propósito. `IOrderStore` começou com `AddAsync` e `GetByIdAsync`, não com `IRepository<T>`; atualmente `AddWithOutboxAsync` representa uma necessidade concreta. `IMessagePublisher` abstrai a borda necessária, sem modelar todos os recursos de brokers.

Essa estrutura permitiu evolução incremental. A mensageria adicionou publisher Azure em `Infrastructure` e worker sem colocar `ServiceBusClient` no domínio; o evento real `OrderCreated` veio depois do probe. O Outbox persiste payload, `MessageId`, `CorrelationId`, subject, content type e contexto W3C junto do pedido. O dispatcher envia após o commit e marca a linha depois da confirmação; falha de envio mantém a linha pendente, enquanto falha ao marcar pode gerar duplicação.

A resposta para duplicação foi idempotência, não a promessa de exactly-once. `EfProcessedMessageStore` usa `(consumer_name, message_id)`, adquire a claim com `INSERT ... ON CONFLICT DO NOTHING`, executa o processamento e grava `Completed` na mesma transação. A conclusão do Service Bus ocorre depois do commit. Payment, Inventory e Notification têm identidades independentes; uma duplicata de Payment não suprime Inventory.

Observabilidade entrou sem invadir o domínio. `CloudOrdersTelemetry` define activities, métricas e correlação; Infrastructure registra exporters e instrumentação. O trace context viaja como metadata da Outbox, sem poluir o payload. Logs usam `OrderId`, `MessageId`, `CorrelationId` e outcome, sem credenciais ou corpos de mensagem.

## PostgreSQL e migrations como fundação

O ADR 0003 escolheu PostgreSQL porque ele é o banco primário planejado e porque o skeleton precisava provar uma fronteira real. EF Core InMemory reduziria o atrito, mas esconderia diferenças de tipos, transações, índices e SQL do provider. `compose.yaml` fornece PostgreSQL 17 localmente, sem transformar a aplicação em script de bootstrap.

As migrations são artefatos versionados em `Infrastructure/Persistence/Migrations`. A aplicação é explícita, por exemplo com `dotnet ef database update --project src/CloudOrders.Infrastructure --startup-project src/CloudOrders.Api -- --environment Development`. A API não chama `Database.Migrate()` no startup e o Compose não cria o schema. O passo manual torna a mudança visível, revisável e controlável.

Os testes de integração seguem a mesma intenção: `PostgreSqlFixture` inicia PostgreSQL 17 via Testcontainers e aplica as migrations. Testes unitários usam doubles, como `CapturingOrderStore`, sem banco. O setup é mais pesado, mas a confiança sobre a infraestrutura é maior.

## Limites sem overengineering

Arquitetura não é sinônimo de quantidade de camadas. O skeleton evitou projeto `Contracts`, generic repository, Unit of Work, domínio completo de itens, autenticação, workers, retry e Azure de produção. Nada disso era necessário para provar o primeiro caminho.

A distinção útil é: fronteira define responsabilidade e direção; abstração cria uma variação ou ponto de troca. `IOrderStore` isolava persistência e permitia teste. `IGenericRepository<TEntity>` seria abstração sem caso de uso. `OrderCreated` virou contrato explícito ao cruzar o processo, mas não exigiu automaticamente novo projeto.

Os limites atuais não resolvem tudo: o dispatcher exige uma instância ativa, não há `FOR UPDATE SKIP LOCKED`, leases, retenção ou replay, e os processadores continuam simulações sem exactly-once externo. O capítulo 10 sintetiza essas restrições no fluxo completo; elas não eram motivo para antecipar complexidade no skeleton.

## Mental model

Pense no CloudOrders como uma sequência de compromissos:

1. `Api` recebe e traduz o mundo externo.
2. `Application` decide qual caso de uso executar.
3. `Domain` protege o significado mínimo de um pedido.
4. Uma porta declara o que é necessário.
5. `Infrastructure` cumpre a porta usando tecnologia concreta.
6. A confirmação HTTP ocorre depois do compromisso exigido.

No estado inicial, o compromisso era “o pedido está persistido”. Hoje, é “pedido e Outbox estão persistidos atomicamente”; broker e consumidores vêm depois, em at-least-once.

## Glossário

- **Walking skeleton:** menor fluxo vertical executável que atravessa as fronteiras essenciais.
- **Vertical slice:** funcionalidade completa de uma entrada até seu efeito, em vez de uma camada inteira isolada.
- **Porta (port):** interface que expressa uma necessidade da aplicação.
- **Adapter:** implementação concreta de uma porta, como `EfOrderStore` ou o publisher Service Bus.
- **Composition root:** ponto que monta o grafo de dependências; no CloudOrders, a API ou cada worker.
- **Outbox:** registro durável que permite publicar depois do commit da transação de negócio.
- **Inbox/idempotência:** estado durável usado para não executar novamente a mesma mensagem por consumidor.
- **At-least-once:** uma mensagem pode ser entregue mais de uma vez; o consumidor precisa tolerar duplicatas.
- **Migration:** versão reproduzível de alteração do schema do banco.
- **Exactly-once:** garantia muito mais forte, não fornecida pelo desenho atual para efeitos externos.

## Perguntas de revisão

1. **Por que o primeiro endpoint retornava `201`, e não `202`?**
   Porque a operação inicial era síncrona e só precisava garantir a persistência do pedido. `202` faria sentido quando o resultado dependesse de processamento assíncrono.

2. **Por que `Domain` não referencia `Infrastructure`?**
   Para que regras de negócio não dependam de EF Core, PostgreSQL, Azure ou ASP.NET Core; isso reduz acoplamento e facilita testes.

3. **O que o Outbox resolve?**
   Evita perder o evento entre o commit do pedido e uma publicação direta. Pedido e intenção de publicação são gravados juntos; o envio posterior ainda pode duplicar.

4. **Por que a chave de idempotência inclui o consumidor?**
   Porque Payment, Inventory e Notification precisam processar independentemente a mesma mensagem publicada em fan-out.

5. **Por que não aplicar migrations automaticamente no startup?**
   Para manter alterações de schema explícitas e controláveis, evitando que cada instância da aplicação faça mutações implícitas no banco.

## Cenários de entrevista

1. **“O envio ao Service Bus confirmou, mas o processo caiu antes de marcar a Outbox como publicada. O que acontece?”**
   Raciocínio sugerido: a linha permanece pendente e pode ser reenviada com o mesmo `MessageId`. Isso é at-least-once; os consumidores devem usar inbox durável para pular a execução duplicada.

2. **“Por que não colocar `ServiceBusClient` no `CreateOrderHandler`?”**
   Raciocínio sugerido: isso mistura caso de uso com adapter de transporte, força testes a conhecer Azure e torna uma troca de broker cara. O handler usa uma porta; a composição injeta a implementação.

3. **“A equipe quer escalar a API para dez réplicas. Basta habilitar o dispatcher em todas?”**
   Raciocínio sugerido: não. O incremento atual suporta uma única instância ativa do dispatcher e não possui mecanismo de claim/lease. É preciso manter apenas uma ativa ou implementar coordenação antes de escalar essa responsabilidade.

## Key takeaways

- Um walking skeleton prova integração real com o menor escopo possível.
- O primeiro CloudOrders atravessava HTTP, Application, Domain, Infrastructure e PostgreSQL, sem mensageria.
- Fronteiras importam quando protegem responsabilidades; abstrações genéricas sem necessidade apenas aumentam o custo.
- PostgreSQL e migrations explícitas deram uma fundação verificável e evolutiva.
- Messaging, workers, Outbox, idempotência e observabilidade foram adicionados depois porque as fronteiras iniciais deixaram pontos de extensão claros.
- O estado atual é assíncrono e at-least-once em partes importantes; isso requer reconhecer trade-offs, não esconder complexidade atrás de nomes.

## Base documental consultada

Este capítulo foi baseado principalmente em:

- `openspec/changes/bootstrap-cloudorders-walking-skeleton/{proposal.md,design.md,tasks.md}` e suas specs de Orders e Health;
- `docs/architecture/adr/0001-target-net-10.md`, `0002-solution-project-boundaries.md`, `0003-postgresql-primary-database.md` e `0004-azure-service-bus-topics-subscriptions.md`;
- `README.md`, `docs/roadmap.md` e `CloudOrders.sln`;
- projetos e implementação atuais em `src/CloudOrders.Api`, `CloudOrders.Application`, `CloudOrders.Domain`, `CloudOrders.Infrastructure` e os quatro workers;
- specs canônicas em `openspec/specs/orders`, `messaging`, `outbox`, `idempotency`, `observability`, `local-development`, `payment`, `inventory` e `notification`;
- testes em `tests/CloudOrders.UnitTests` e `tests/CloudOrders.IntegrationTests`, incluindo `PostgreSqlFixture`.
