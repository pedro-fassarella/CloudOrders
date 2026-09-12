# Design: Mensageria Azure Service Bus

## Fluxo e limites

Em Development, `POST /messaging/probe` cria `MessagingProbe` e `MessageMetadata`, chama `IMessagePublisher` e retorna `202 Accepted` somente depois que o publisher conclui o envio. O endpoint existe somente para validar o transporte; `POST /orders` nao o chama e nao publica `OrderCreated`.

`CloudOrders.Application` contem somente `MessagingProbe`, `MessageMetadata` e `IMessagePublisher`. `CloudOrders.Infrastructure` implementa a porta com `AzureServiceBusMessagePublisher`, serializa o payload com `System.Text.Json` e cria `ServiceBusMessage` com `MessageId`, `CorrelationId`, `Subject` e `ContentType`. Domain nao recebe referencias nem tipos relacionados a Azure.

O novo `CloudOrders.Messaging.Worker` e o limite de transporte de consumo. Ele referencia Application para o contrato do probe, Infrastructure para configuracao e registro do cliente, e usa o SDK Azure apenas para o processor. Nao sera criado projeto Contracts nem framework generico de publishers ou consumers.

## Topic, filtro e ciclo de vida

O topic e `order-events` e a subscription e `messaging-probe`. A subscription remove a regra `$Default` e usa uma regra SQL para `sys.Label = 'CloudOrders.Messaging.Probe'`. O SDK atribui esse label por meio de `ServiceBusMessage.Subject`, isolando o worker temporario de eventos futuros.

`ServiceBusClient` e `ServiceBusSender` sao singletons registrados por DI e descartados com o host. A API somente resolve o cliente quando o endpoint de probe e chamado, preservando os testes existentes sem configuracao Azure. O worker resolve o cliente ao iniciar e falha de forma clara se a configuracao local estiver ausente.

O worker cria um `ServiceBusProcessor` de longa duracao para a combinacao topic/subscription, com `AutoCompleteMessages = false`, `MaxConcurrentCalls = 1` e Peek-Lock padrao. Depois de validar subject e content type, desserializar o JSON e registrar o probe, ele chama `CompleteMessageAsync`. Excecoes acontecem antes de completion; esta mudanca nao implementa abandon, retry, DLQ ou idempotencia customizados.

## Configuracao e recursos locais

As chaves sao `ConnectionStrings:ServiceBus`, `Messaging:TopicName` e `Messaging:SubscriptionName`. Appsettings comprometidos contem somente a connection string vazia e os nomes seguros. API e Worker possuem User Secrets IDs proprios; `ConnectionStrings__ServiceBus` e as chaves `Messaging__*` podem sobrescrever os valores localmente.

README instrui a criar manualmente um namespace Service Bus Standard, o topic, a subscription, a regra de filtro e uma politica SAS local com Send/Listen. A connection string e salva somente em User Secrets. `.env` continua exclusivo do Docker Compose. Managed Identity e RBAC sao a direcao para deployment futuro, mas Azure.Identity, Key Vault e recursos de producao nao entram nesta mudanca.

## Testes e ADR

O teste unitario cria uma mensagem Service Bus sem conexao de rede e confirma o round-trip JSON e os quatro metadados nativos. Os testes de integracao existentes continuam com PostgreSQL Testcontainers e nao recebem configuracao Service Bus. O smoke test manual inicia API e Worker, envia o probe, confere IDs nos logs e confirma a remocao da mensagem ativa na subscription.

ADR 0004 registra Topic/Subscription como decisao arquitetural. Nao ha ADR para o SDK, `System.Text.Json` ou tempos de vida de DI.
