# Consolidar Persistencia de Orders

## Objetivo

Consolidar o vertical slice de Orders existente: consultar pedidos persistidos por identificador, verificar a conectividade do PostgreSQL em `/health` e executar os testes de integracao contra PostgreSQL efemero gerenciado por Testcontainers.

## Motivacao

O walking skeleton ja possui `CloudOrdersDbContext`, o mapeamento de `Order`, a migration inicial e criacao persistida. Porem, ainda nao expoe leitura por ID, o health check e apenas de processo e os testes de integracao dependem de banco iniciado manualmente. Esta mudanca torna essas fronteiras verificaveis sem ampliar o dominio.

## Escopo

- `GET /orders/{id}` para recuperar um pedido existente.
- Extensao de `IOrderStore` com consulta por ID e implementacao EF Core sem tracking.
- Health check de `CloudOrdersDbContext`/PostgreSQL em `/health`.
- Testcontainers PostgreSQL para todos os testes de integracao que usam banco, aplicando a migration existente.
- Atualizacao minima do README e das dependencias centralizadas.

## Fora de escopo

- Alterar `compose.yaml` ou `.env.example`.
- Criar ou alterar migrations e schema.
- Atualizar, excluir ou listar pedidos.
- Mensageria, workers, Outbox, observabilidade, Azure, CI/CD e infraestrutura como codigo.

## Criterios de sucesso

1. `POST /orders` continua persistindo e retornando `201 Created`.
2. `GET /orders/{id}` retorna `200` para pedido existente e `404` para inexistente.
3. `/health` retorna `200` quando o PostgreSQL esta disponivel e usa o mecanismo padrao de health check para reportar indisponibilidade.
4. A suite de integracao inicia, migra e descarta um PostgreSQL isolado sem `docker compose up` ou configuracao manual de connection string.
5. Restore, build, testes e auditoria NuGet concluem sem vulnerabilidades conhecidas.

## Decisoes adiadas

O padrao de Docker Compose para desenvolvimento local sera revisado em `add-local-docker-environment`. PostgreSQL gerenciado pertence ao futuro ambiente cloud.
