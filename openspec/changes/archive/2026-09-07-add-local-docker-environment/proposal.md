# Formalizar Ambiente Docker Local

## Objetivo

Formalizar o ambiente local de desenvolvimento com PostgreSQL 17 em Docker Compose, mantendo `CloudOrders.Api` executando pelo Visual Studio e preservando migrations explicitas e testes de integracao isolados por Testcontainers.

## Motivacao

O repositorio ja possui um servico PostgreSQL local, mas o fluxo de configuracao, a porta do host, o ciclo de vida do volume e a separacao entre Compose, User Secrets e Testcontainers ainda nao estao documentados de forma completa. A factory de EF tambem nao reproduz a configuracao usada pela API.

## Escopo

- Padronizar o servico PostgreSQL local no Compose com PostgreSQL 17, health check, volume nomeado e porta configuravel.
- Manter `.env` como configuracao exclusiva do Compose e seguro para Git.
- Configurar `ConnectionStrings:Postgres` por User Secrets durante o desenvolvimento, com override por environment variable.
- Fazer o EF tooling usar a mesma pipeline de configuracao padrao da API.
- Documentar migrations explicitas, verificacao de health, persistencia de dados e reset intencional.
- Registrar os requisitos e tarefas desta mudanca no OpenSpec.

## Fora de escopo

- Containerizar a API, criar Dockerfile, `.dcproj`, rede Docker customizada ou outros servicos no Compose.
- Alterar schema, criar migrations, executar `Database.Migrate()` no startup ou usar scripts de inicializacao de schema.
- Alterar a estrategia de Testcontainers, Azure, mensageria, workers, Redis, Outbox, CI/CD, IaC ou secrets de producao.
- Criar ADR adicional.

## Criterios de sucesso

1. Um desenvolvedor consegue iniciar PostgreSQL com Compose usando uma configuracao local nao versionada.
2. A API executada pelo Visual Studio conecta ao PostgreSQL local por `ConnectionStrings:Postgres` sem credenciais comprometidas.
3. A migration e aplicada somente por comando EF explicito.
4. O volume persiste dados apos `docker compose down` e so e removido intencionalmente por `docker compose down -v`.
5. Testes de integracao continuam iniciando PostgreSQL 17 isolado por Testcontainers, sem depender do Compose.
