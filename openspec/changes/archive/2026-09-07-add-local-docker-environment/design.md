# Design: Ambiente Docker Local

## Fluxo local

O fluxo principal e `Docker Compose -> PostgreSQL` e `Visual Studio -> CloudOrders.Api`. O Compose executa somente o banco; a API continua um processo local com os perfis existentes de Development. Nao sera criado Dockerfile, servico de API no Compose, `.dcproj`, rede customizada ou dependencia da API em health check do Compose.

O servico `postgres` usa `postgres:17`, o mesmo major usado por `PostgreSqlFixture` nos testes de integracao. Ele expoe a porta interna 5432 na porta de host configurada por `POSTGRES_PORT` (5432 por padrao), usa `cloudorders_pg` como volume nomeado, reinicia com `unless-stopped` e usa `pg_isready` como health check de readiness.

## Configuracao da aplicacao e EF tooling

`ConnectionStrings:Postgres` permanece a unica chave de connection string. `appsettings.json` continua sem credenciais comprometidas.

`CloudOrdersDbContextFactory` ficara no projeto de startup da API e criara `WebApplicationBuilder` da mesma forma que a aplicacao. Assim, a factory nao mantem uma lista paralela de providers ou faz parsing manual de `.env`; ela usa a pipeline de configuracao padrao do host.

A precedencia relevante e:

1. `appsettings.json` e `appsettings.{Environment}.json` comprometidos, incluindo `appsettings.Development.json` quando o ambiente for Development;
2. User Secrets no ambiente Development;
3. environment variables.

Portanto, `ConnectionStrings__Postgres` sobrescreve o valor de `ConnectionStrings:Postgres` configurado em User Secrets. A API recebe um `UserSecretsId`; o comando EF deve receber `--environment Development` para carregar os mesmos User Secrets. Se a chave continuar ausente, a API e a factory falham com uma mensagem clara em vez de usar uma connection string sem senha.

`.env` nao e uma fonte de configuracao da aplicacao. Docker Compose a utiliza para interpolar `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` e `POSTGRES_PORT`. O desenvolvedor configura em User Secrets uma connection string com os mesmos valores de banco, usuario, senha e porta.

## Volume, migrations e health checks

`POSTGRES_PASSWORD=replace-with-a-local-password` em `.env.example` e somente um placeholder seguro. O README instrui o desenvolvedor a substitui-lo antes da primeira inicializacao do volume.

PostgreSQL usa `POSTGRES_DB`, `POSTGRES_USER` e `POSTGRES_PASSWORD` somente quando o diretorio de dados esta vazio. Alterar esses valores depois que `cloudorders_pg` foi inicializado nao reconfigura o banco existente. `docker compose down` preserva o volume; `docker compose down -v` e o reset explicito e remove todos os dados locais.

O container nao aplica schema da aplicacao. Migrations continuam explicitas com `dotnet ef database update --project src/CloudOrders.Infrastructure --startup-project src/CloudOrders.Api -- --environment Development`. Nao sera adicionado `Database.Migrate()` ao startup.

O health check do Compose verifica readiness do processo PostgreSQL. O endpoint `/health` permanece usando `AddDbContextCheck<CloudOrdersDbContext>` e verifica a conectividade efetiva da API com PostgreSQL; os dois niveis nao sao equivalentes.

## Testes e ADR

`PostgreSqlFixture` continua iniciando PostgreSQL 17 efemero por Testcontainers, aplica a migration existente e fornece sua propria connection string ao `WebApplicationFactory`. A suite nao le `.env`, `POSTGRES_*` ou o banco do Compose.

ADR 0003 ja registra PostgreSQL e Docker Compose para desenvolvimento local. Esta mudanca o referencia e nao cria ADR adicional.
