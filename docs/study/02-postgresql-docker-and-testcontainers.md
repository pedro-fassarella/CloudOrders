# PostgreSQL, Docker e Testcontainers no CloudOrders

Persistência é uma decisão de comportamento, não apenas uma escolha de pacote. O CloudOrders precisava provar que um pedido sobrevivia ao processo da API e que a aplicação conversava com o banco definido para sua evolução. PostgreSQL entrou no walking skeleton; Compose e Testcontainers resolveram, respectivamente, desenvolvimento manual e testes isolados.

Este capítulo descreve o repositório atual. `add-local-docker-environment` formalizou Compose, e `add-orders-persistence` consolidou leitura por ID, health check conectado ao banco e testes com PostgreSQL efêmero. As tabelas posteriores de mensagens processadas e Outbox são extensões; não pertenciam à primeira tabela `orders`.

## Por que PostgreSQL é o banco primário

O ADR 0003 registra duas razões. O plano de longo prazo do CloudOrders define PostgreSQL como banco primário, e o walking skeleton precisava testar uma fronteira real de persistência sem introduzir ainda uma plataforma de produção ampla. Usar o banco escolhido desde o começo reduz o risco de validar um comportamento em um provider artificial e descobrir incompatibilidades apenas depois.

Isso explica a combinação PostgreSQL 17 + EF Core 10 + Npgsql. O sistema não precisa de um schema grande para provar a decisão: a migration inicial cria apenas `orders`, com `Id`, `CustomerId`, `CreatedAtUtc` e `Status`. O domínio continua pequeno, mas o adapter de persistência já executa contra o mecanismo real. Managed PostgreSQL, private networking, identity e hardening de produção continuam fora do escopo documentado.

Há um custo: o desenvolvedor precisa de Docker e de uma senha local. Esse custo é aceito porque os testes de integração e o ambiente local usam a mesma família de banco, tornando a diferença entre “funciona no fake” e “funciona no provider real” menor.

## EF Core e `CloudOrdersDbContext`

`CloudOrders.Infrastructure/Persistence/CloudOrdersDbContext.cs` é o ponto de entrada do EF Core. Ele herda de `DbContext` e expõe `DbSet<Order> Orders`; hoje também expõe `ProcessedMessages` e `OutboxMessages`, adicionados posteriormente.

`OnModelCreating` transforma objetos em schema explícito. `Order` vai para `orders`; `Id` é a chave; `CustomerId` é obrigatório e limitado a 100; `CreatedAtUtc` é obrigatório; e o status é texto, limitado a 32. Assim, `Pending` fica legível e não depende do ordinal do enum.

O contexto mapeia `processed_messages` com chave composta `(consumer_name, message_id)` e check constraint para `Processing` ou `Completed`. `outbox_messages` tem chave primária, índice único para `MessageId`, índices de consulta e check constraint `attempt_count >= 0`. Isso sustenta idempotência e publicação posterior, mas não fazia parte da superfície mínima inicial.

Em `Infrastructure/DependencyInjection.cs`, `AddInfrastructure` exige `ConnectionStrings:Postgres`, configura `UseNpgsql` e registra `EfOrderStore` como `IOrderStore`. `CloudOrders.Api/Program.cs` compõe os serviços e registra `AddDbContextCheck<CloudOrdersDbContext>` para o `/health` atual.

`EfOrderStore` concentra operações de Orders. `AddWithOutboxAsync` cria `OutboxMessage`, inicia transação, adiciona Order e Outbox, executa `SaveChangesAsync` e faz commit; em erro, faz rollback. `GetByIdAsync` usa `AsNoTracking()` e `SingleOrDefaultAsync` para leitura sem alteração.

No walking skeleton original, a operação de escrita era `AddAsync(Order)`. O acréscimo de `AddWithOutboxAsync` veio depois, quando a regra passou a ser “pedido e intenção de publicação devem confirmar juntos”. A mudança de interface é uma evolução de uma necessidade concreta, não evidência de que a primeira arquitetura estava incompleta.

## `IOrderStore` não é um generic repository

`IOrderStore` está em `CloudOrders.Application/Orders/CreateOrder.cs`. Ele é uma porta orientada ao slice de Orders: expõe as operações que os casos de uso realmente precisam. Na fase inicial, eram criação e consulta por ID; atualmente, a criação inclui a Outbox.

Um generic repository costuma oferecer `Add`, `Get`, `Find`, `Update` e `Delete` para qualquer entidade, mesmo sem casos de uso para elas. Isso cria API artificial, espalha decisões e esconde consultas atrás de uma camada que não conhece o domínio. O CloudOrders evitou `IRepository<T>` e Unit of Work genérico.

Nos testes, `CreateOrderHandlerTests` fornece `CapturingOrderStore`, controla `TimeProvider` e verifica Order, `OrderCreated`, metadados e trace sem EF Core. O adapter real fica nos testes de integração. A porta é pequena porque a necessidade é pequena.

## Desenvolvimento manual: Visual Studio + Docker Compose

O Compose do CloudOrders executa somente PostgreSQL. A API permanece um processo local iniciado pelo Visual Studio, com os perfis de Development já existentes; não há Dockerfile da API, `.dcproj`, rede customizada ou container para a aplicação nesse fluxo.

```text
Desenvolvedor
    |
    +--> docker compose up -d postgres
    |         |
    |         +--> PostgreSQL 17
    |                volume nomeado: cloudorders_pg
    |
    +--> Visual Studio / dotnet run
              |
              +--> CloudOrders.Api
                       |
                       +--> ConnectionStrings:Postgres
                                  |
                                  +--> localhost:5432
```

O serviço `postgres` usa a imagem `postgres:17`, expõe a porta interna 5432 em uma porta de host configurável por `POSTGRES_PORT` — 5432 por padrão — e tem `restart: unless-stopped`. O health check usa `pg_isready`, portanto indica readiness do PostgreSQL, não apenas que o processo do container foi iniciado.

O fluxo documentado começa copiando `.env.example` para `.env` e substituindo `POSTGRES_PASSWORD=replace-with-a-local-password`. O Compose lê `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` e `POSTGRES_PORT` para interpolar o serviço. A aplicação não lê `.env`; ela recebe uma connection string separadamente.

## Volume nomeado e o ciclo de vida do banco

`cloudorders_pg:/var/lib/postgresql/data` é um named volume. O diretório de dados fica fora da camada descartável do container. Assim, `docker compose down` remove o container, mas conserva o volume; ao executar novamente `docker compose up -d postgres`, o mesmo cluster PostgreSQL e seus Orders reaparecem.

Isso é conveniente para desenvolvimento, mas confunde. A imagem oficial usa `POSTGRES_DB`, `POSTGRES_USER` e `POSTGRES_PASSWORD` apenas ao inicializar um diretório vazio. Depois, alterar `.env` muda as variáveis do novo processo, não o usuário, banco ou senha gravados no cluster existente.

Uma senha nova no `.env` pode causar autenticação mesmo com o container “healthy”. Alinhe a connection string ao banco, altere a senha dentro do PostgreSQL ou faça reset. `docker compose down -v` remove volume e dados locais; é o reset destrutivo documentado.

## User Secrets e precedência de configuração

Conceitualmente, a configuração segue: `appsettings.json` e `appsettings.{Environment}.json`, User Secrets em Development e, por fim, environment variables. O valor mais à direita vence.

A API tem `UserSecretsId`; o README orienta armazenar ali `ConnectionStrings:Postgres` com os valores do `.env`. `ConnectionStrings__Postgres` representa a mesma chave via environment variable e sobrescreve o User Secret. `.env` é do Compose, não da aplicação.

Os exemplos deste capítulo preservam senhas e connection strings como placeholders. A configuração local deve ficar em `.env`, User Secrets ou environment variables do desenvolvedor, sem copiar valores locais para a documentação.

`CloudOrdersDbContextFactory` é a factory de design time. Ela cria `WebApplicationBuilder` com a identidade da API, lê `GetConnectionString("Postgres")` e aplica `UseNpgsql`, reutilizando a pipeline da API. Em Development, `-- --environment Development` carrega o mesmo ambiente e User Secrets.

## Por que as migrations são explícitas

O schema da aplicação não é criado pelo container e a API não chama `Database.Migrate()` durante o startup. A aplicação de schema é uma ação consciente, por exemplo:

```powershell
dotnet ef database update `
  --project src/CloudOrders.Infrastructure `
  --startup-project src/CloudOrders.Api `
  -- --environment Development
```

Isso evita que uma réplica altere o banco ao iniciar. A migration é revisável, controlada e deixa histórico. A sequência atual inclui `20260907141101_InitialCreate`, `20260908135434_AddProcessedMessages` e `20260912213142_AddOutboxMessages`.

`add-orders-persistence` não criou migration: `orders` e `InitialCreate` já existiam. A mudança adicionou leitura, health check e fixture, validando ausência de pending model changes. Nem toda mudança de aplicação é mudança de schema.

## Testcontainers e testes isolados

Compose representa um banco de trabalho persistente e controlado pelo desenvolvedor. Testcontainers representa uma dependência efêmera, criada e destruída pela suite. Os dois usam PostgreSQL 17, mas não compartilham dados nem configuração.

```text
dotnet test CloudOrders.IntegrationTests
    |
    +--> PostgreSqlFixture
             |
             +--> inicia PostgreSQL 17 efêmero
             +--> obtém connection string do container
             +--> CloudOrdersDbContext.UseNpgsql(...)
             +--> Database.MigrateAsync()
             +--> WebApplicationFactory<Program>
                         |
                         +--> API aponta para o banco efêmero
                         +--> fixture descarta o container
```

`PostgreSqlFixture` usa `PostgreSqlBuilder("postgres:17")`, inicia o container, configura `CloudOrdersDbContext` com a connection string fornecida e aplica `MigrateAsync`; `DisposeAsync` descarta o container. `IntegrationTestFactory` injeta a connection string, seleciona `Testing`, desabilita o Outbox Dispatcher e captura o publisher.

Isso testa `POST /orders`, `GET /orders/{id}` e `/health` contra PostgreSQL real sem `docker compose up`, `.env` ou banco pessoal. A suite verifica persistência da Order, Outbox pendente, rollback de inserção da Outbox, `404`, `400` e cenários de rollback/concorrência em `ProcessedMessageStoreTests`.

Os testes unitários têm outro propósito. `OrderTests` exercita invariantes de `Order` — inclusive trim de `customerId` e rejeição de valor ausente — sem rede ou banco. `CreateOrderHandlerTests` verifica coordenação usando doubles e tempo controlado. Eles são rápidos e localizam regra ou colaboração. Os testes de integração verificam wiring HTTP, EF Core, Npgsql, migrations e transação. Um tipo de teste não substitui o outro.

## Por que “funciona no meu Compose” não basta

Um pedido criado na base local prova apenas aquela combinação de volume, migrations aplicadas, credenciais, porta e dados existentes. Ela pode esconder schema antigo, dados contaminantes, senha divergente ou migration aplicada manualmente.

Testcontainers começa com PostgreSQL novo e aplica migrations versionadas, usando connection string repetível mesmo com Compose parado. Isso dá evidência melhor sobre schema e transações. Não prova disponibilidade, tuning, backup ou rede de produção, que o ADR 0003 deixa para depois.

A evidência correta é: “a aplicação inicializa PostgreSQL limpo com as migrations atuais e o contrato HTTP persiste e recupera o pedido em teste automatizado”. Compose continua valioso, mas não deve ser a única evidência.

## Falhas comuns e troubleshooting

**Conexão recusada ou porta incorreta.** Verifique `docker compose config`, `docker compose ps` e `docker compose logs postgres`. Se `POSTGRES_PORT` não for 5432, altere a porta na connection string. Container “healthy” não corrige host ou porta errados na API.

**Falha de autenticação após trocar a senha.** Compare a connection string com as credenciais da inicialização do volume. Editar `.env` não altera credenciais persistidas. Preserve o volume se os dados importarem; use `down -v` apenas para reset destrutivo.

**Tabela ausente ou migration não aplicada.** Inicie PostgreSQL, configure User Secrets e execute `dotnet ef database update` com startup project e ambiente Development. Compose cria o servidor, não `orders`, `processed_messages` ou `outbox_messages`.

**Schema drift.** Compare `CloudOrdersDbContext` com `CloudOrdersDbContextModelSnapshot` e execute `dotnet ef migrations has-pending-model-changes`. A validação de `add-orders-persistence` encontrou o modelo alinhado. Se mudar, crie e revise migration; não dependa de edição manual.

**Teste falha antes de executar a API.** Testcontainers não depende do Compose, mas precisa de Docker-compatible runtime. Sem engine, a fixture não cria PostgreSQL; isso é falha do ambiente de teste, não da connection string da API.

## Trade-offs do modelo local

O arranjo é simples, mas não gratuito. Compose com volume preserva dados, porém pode deixar estado antigo e exigir disciplina de reset. User Secrets protege credenciais fora do Git, mas requer configuração por desenvolvedor. Migrations explícitas aumentam passos em troca de controle.

Testcontainers torna a suite repetível, mas requer Docker e aumenta o setup. PostgreSQL real aumenta fidelidade e custo frente a InMemory. Não confunda conveniência do Compose com cobertura automatizada ou isolamento de Testcontainers com produção.

## Mental model

Pense em três camadas de confiança:

1. **Modelo:** unit tests provam regras de `Order` e coordenação do handler.
2. **Provider:** Testcontainers prova EF Core, Npgsql, migrations, transações e HTTP contra PostgreSQL limpo.
3. **Trabalho diário:** Compose fornece um PostgreSQL persistente para executar a API no Visual Studio, investigar e repetir cenários.

Migrations fazem a ponte entre modelo e provider; User Secrets fazem a ponte entre configuração e processo; o volume faz a ponte entre containers descartáveis e dados locais. Nenhuma dessas pontes deve ser confundida com garantia de produção.

## Glossário

- **EF Core:** ORM usado para mapear entidades, consultas, transações e migrations.
- **`DbContext`:** unidade de trabalho do EF Core que expõe conjuntos e configura o modelo.
- **Npgsql:** provider que conecta EF Core ao PostgreSQL.
- **Migration:** versão reproduzível de alteração do schema.
- **Design time:** execução de ferramentas como `dotnet ef`, fora do startup normal da API.
- **Docker Compose:** definição declarativa para executar o PostgreSQL local.
- **Named volume:** armazenamento persistente associado ao serviço, separado da camada do container.
- **Testcontainers:** biblioteca que cria dependências reais efêmeras em containers durante testes.
- **Schema drift:** divergência entre o modelo do código, migrations e banco aplicado.
- **Generic repository:** abstração ampla de CRUD que não necessariamente representa uma necessidade do domínio.

## Perguntas de revisão

1. **Por que o CloudOrders usa PostgreSQL desde o walking skeleton?**
   Porque PostgreSQL é o banco primário planejado e o projeto precisava provar uma fronteira real de persistência com o provider escolhido.

2. **O que `AsNoTracking()` comunica em `GetByIdAsync`?**
   Que a consulta é somente leitura; o contexto não precisa acompanhar mudanças naquela entidade.

3. **Por que `docker compose down` preserva Orders?**
   Porque o serviço usa o named volume `cloudorders_pg`, que não é removido pelo down normal.

4. **Por que a senha do `.env` pode não coincidir com a senha efetiva?**
   Porque as variáveis `POSTGRES_*` inicializam o banco apenas quando o diretório de dados está vazio; um volume existente mantém o cluster e suas credenciais.

5. **Qual é a diferença principal entre `OrdersApiTests` e `OrderTests`?**
   O primeiro verifica o caminho integrado HTTP + EF Core + PostgreSQL; o segundo verifica regras e coordenação com doubles, sem infraestrutura externa.

## Cenários de entrevista

1. **“A equipe quer adicionar `UpdateAsync`, `DeleteAsync` e `FindAllAsync` ao `IOrderStore` antes de qualquer requisito. Como você responde?”**
   Raciocínio sugerido: pergunte por casos de uso concretos. A porta deve ser orientada ao slice atual; ampliar por antecipação recria o problema do generic repository e aumenta a superfície de acoplamento.

2. **“A migration está no projeto, mas uma API nova recebe ‘relation orders does not exist’. Qual a sequência de investigação?”**
   Raciocínio sugerido: confirmar container, porta, credenciais e connection string efetiva; verificar `docker compose ps`/logs; então aplicar `dotnet ef database update` com o startup project e ambiente corretos. Não adicionar migration automática ao startup como atalho.

3. **“Por que os testes não apontam para o mesmo banco do Compose para serem mais realistas?”**
   Raciocínio sugerido: o banco compartilhado introduz estado e ordem de execução. Testcontainers fornece PostgreSQL 17 limpo, aplica migrations e destrói o recurso, mantendo realismo do provider com isolamento e repetibilidade.

## Key takeaways

- PostgreSQL foi escolhido como fundação porque é o provider primário planejado e testa a fronteira real de persistência.
- `CloudOrdersDbContext` concentra o mapeamento; `EfOrderStore` expõe apenas as operações de Orders necessárias.
- Migrations explícitas tornam mudanças de schema revisáveis e evitam mutações ocultas no startup.
- Compose é o banco persistente do desenvolvimento manual; Testcontainers é o banco efêmero e isolado da integração.
- Named volumes preservam dados, mas também preservam credenciais e estado antigo; alterar `.env` não reconfigura um volume inicializado.
- Unit tests e integration tests respondem perguntas diferentes.
- “Funciona no Compose” é um sinal útil, não uma prova suficiente de correção de persistência.
