# Orders API Specification

## POST /orders

`POST /orders` preserva o contrato existente: um `customerId` nao vazio cria e persiste um pedido `Pending` e retorna `201 Created` com `id`, `customerId`, `status` e `createdAtUtc`. O header `Location` aponta para `/orders/{id}`.

Entrada ausente, vazia ou composta por espacos retorna `400 Bad Request` em `ProblemDetails` e nao persiste pedido.

## GET /orders/{id}

Quando `id` identifica um pedido persistido, a API retorna `200 OK` com:

```json
{
  "id": "00000000-0000-0000-0000-000000000000",
  "customerId": "customer-123",
  "status": "Pending",
  "createdAtUtc": "2026-09-07T12:00:00+00:00"
}
```

Quando nao existir pedido para um GUID valido, a API retorna `404 Not Found`.

Quando o segmento `id` nao puder ser associado a `Guid`, a API preserva o comportamento padrao do binding de Minimal APIs e retorna `400 Bad Request`; a especificacao nao exige corpo ou validacao customizada.

Nao sao incluidos endpoints de listagem, atualizacao ou exclusao.
