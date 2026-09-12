# Design: Consolidacao de persistencia de Orders

## Dependencias e fluxo

`Api` permanece a composicao raiz e depende de `Application` e `Infrastructure`. `Application` depende somente de `Domain` e contem os handlers de criacao e consulta. `Infrastructure` implementa a porta especifica `IOrderStore` com EF Core/Npgsql. `Domain` permanece independente de EF Core, PostgreSQL e ASP.NET Core.

O fluxo de leitura sera `GET /orders/{id}` -> `GetOrderHandler` -> `IOrderStore.GetByIdAsync` -> `CloudOrdersDbContext` -> PostgreSQL. A consulta EF usara `AsNoTracking`, pois nenhum estado sera alterado. Nao sera introduzido repositorio generico nem Unit of Work: `IOrderStore` ja e a porta focada em Orders e deve expor apenas as operacoes exigidas pelo slice.

## Persistencia e configuracao

O modelo, mapeamento e migration `InitialCreate` existentes ja representam `orders` com `Id`, `CustomerId`, `CreatedAtUtc` e `Status`; portanto, esta mudanca nao cria migration. A configuracao continua em `ConnectionStrings:Postgres`, consumida pela configuracao padrao do .NET, User Secrets ou variaveis de ambiente.

Os pacotes Microsoft relacionados ao framework serao alinhados centralmente em `10.0.11`, incluindo EF Core, EF Design, ASP.NET Core testing/OpenAPI e `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`. O provider sera `Npgsql.EntityFrameworkCore.PostgreSQL` `10.0.3`. `Testcontainers.PostgreSql` `4.14.0` sera referenciado somente pelo projeto de integracao.

## Health check

A API registrara `AddDbContextCheck<CloudOrdersDbContext>` junto aos health checks e manterá `MapHealthChecks("/health")`. O comportamento de indisponibilidade e o status `503` ficam a cargo do mecanismo padrao do ASP.NET Core. Nao havera teste artificial de banco indisponivel: configurar uma conexao deliberadamente inacessivel adicionaria timeout ou dependencia de rede sem aumentar a confiabilidade da suite.

## Testes de integracao

Uma fixture Testcontainers criara PostgreSQL 17 para a classe/suite de integracao, aplicara explicitamente a migration existente com `Database.MigrateAsync`, fornecera a connection string ao `WebApplicationFactory` e descartara o container com `IAsyncLifetime`. A fixture elimina qualquer leitura de `ConnectionStrings__Postgres` ou `POSTGRES_*` e nao usa EF Core InMemory.

Testcontainers e exclusivo de testes efemeros e isolados. `compose.yaml` e `.env.example` permanecem intactos como artefatos existentes para desenvolvimento local futuro. PostgreSQL gerenciado permanece uma decisao do ambiente cloud futuro.

## HTTP

`GET /orders/{id}` recebera `Guid id` diretamente pelo binding de Minimal APIs. GUID invalido preserva a resposta padrao `400`, sem validacao customizada ou promessa de payload especifico. Pedido inexistente retorna `404`; pedido existente retorna o mesmo formato publico de Order usado na criacao. O `Location` de `POST /orders` ja aponta para essa rota e passara a ser resolvivel.
