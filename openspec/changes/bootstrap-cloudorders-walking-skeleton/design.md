# Design do Walking Skeleton

## Estrutura e dependências

`CloudOrders.Domain` não possui dependências de aplicação ou infraestrutura. `CloudOrders.Application` contém o caso de uso de criação e a porta `IOrderStore`. `CloudOrders.Infrastructure` implementa essa porta com `CloudOrdersDbContext` e PostgreSQL. `CloudOrders.Api` é a composição raiz e registra as dependências.

Não será criado um projeto `Contracts` separado porque só existe um endpoint e um contrato HTTP nesta etapa.

## Modelo mínimo

`Order` contém `Id`, `CustomerId`, `CreatedAtUtc` e `Status`. O único status inicial é `Pending`. A entidade valida o identificador do cliente e recebe o horário do caso de uso via `TimeProvider`.

## Persistência

PostgreSQL e EF Core 10 entram agora porque tornam verificável a fronteira de infraestrutura e alinham o walking skeleton ao banco escolhido para a evolução futura. O custo adicional fica limitado a um `DbContext`, uma tabela, uma migration e uma configuração local.

Não haverá repositório genérico. `IOrderStore` expõe somente a operação necessária para criar um pedido.

## Docker e configuração

`compose.yaml` executa somente PostgreSQL 17. O arquivo `.env.example` contém placeholders; o `.env` real é ignorado pelo Git. A API lê `ConnectionStrings:Postgres` pela configuração padrão do .NET, permitindo User Secrets ou variável de ambiente.

O Dockerfile da API e o deploy em containers serão tratados em uma mudança posterior para manter o primeiro incremento pequeno.

## HTTP

O endpoint síncrono retorna `201 Created`. O `202 Accepted` do plano legado só será apropriado quando a publicação de eventos e o processamento assíncrono existirem. O erro de validação usa `ProblemDetails`.

## Testes

Os testes unitários usam doubles em memória e não exigem PostgreSQL. Os testes de integração usam `WebApplicationFactory`; o teste de persistência exige `ConnectionStrings__Postgres` ou as variáveis `POSTGRES_DB`, `POSTGRES_USER` e `POSTGRES_PASSWORD`, apontando para o PostgreSQL iniciado pelo Compose.

## Compatibilidade

O plano legado menciona .NET 8, mas esta implementação usa .NET 10, SDK 10.0.301 e pacotes da linha 10.x. O documento legado não será reescrito nesta mudança.
