# ADR 0004 — RabbitMQ como fila alternativa, sem revogar a ADR 0001

- **Status:** Aceita
- **Data:** 2026-09-29
- **Contexto do plano:** Pós-etapa 4 — hardening rumo à apresentação na SRM
- **Relacionada a:** ADR 0001 (PostgreSQL como fila), PRD §11 (`IProcessingQueue`), RF-007,
  `src/DocReader.Infrastructure/Queue`, `apps/worker/ProcessingWorker.cs`, `docker-compose.rabbitmq.yml`

## Contexto

A ADR 0001 escolheu `processing_jobs` no PostgreSQL como fila da PoC e listou, como gatilho de revisão
explícito, "mais de um processo consumidor competindo em escala real, necessidade de dead-letter ou
fanout, tempo de espera dominado por contenção no banco, ou promoção da PoC a ambiente produtivo com
SLA" — e fechou dizendo que a troca "deve custar uma nova implementação de `IProcessingQueue` e nenhuma
mudança em `Domain`, `Application` ou na API pública". Esta ADR é exatamente essa troca, oferecida como
**alternativa opt-in**, não substituição: quem quer familiaridade operacional com um broker de verdade,
ou fanout/roteamento que a fila em SQL não tem, liga `QUEUE_PROVIDER=RabbitMQ`; quem não pede nada disso
continua em PostgreSQL, comportamento byte-a-byte igual ao de antes desta ADR. **A ADR 0001 permanece
válida**: PostgreSQL é o padrão, e nada aqui a revoga.

A restrição de projeto era rígida: zero mudança em `src/DocReader.Domain` e `src/DocReader.Application`,
nem para adicionar uma chamada. Isso é possível porque só quatro dos seis métodos de `IProcessingQueue`
são realmente chamados pelo caminho de produção hoje (`AcquireNextAsync`, `HeartbeatAsync`, `FailAsync`,
`ReleaseAsync`, todos de `apps/worker`/`DocumentProcessor`), e os dois que faltam
(`EnqueueAsync`/`CompleteAsync`) já são código morto na Etapa 3: o enfileiramento de verdade é
`DocumentRepository.AcceptAsync` gravando a linha de `processing_jobs` direto, e a conclusão de verdade é
`DocumentProcessingStore.CompleteAsync`, uma interface diferente. Essa folga é o que permite hospedar
todo o mecanismo do RabbitMQ em `DocReader.Infrastructure` e `apps/worker`.

## Decisão

### Seleção de provedor

`QUEUE_PROVIDER` (`Postgres`, padrão, ou `RabbitMQ`) é lido direto de `IConfiguration`
(`DocReader:Queue:Provider`) em `AddDocReaderInfrastructure`, no mesmo estilo que essa mesma função já lê
a connection string — não por um binding de `IOptions<ProcessingQueueOptions>`, porque essa classe é de
`DocReader.Application` e esta escolha é só fiação de `Infrastructure`. O resultado vira um singleton
simples (`QueueProviderOptions`, um `record` com um enum `QueueProvider`), não um `IOptions<T>`: o valor
nunca muda em runtime, e nada em `Application` precisa vê-lo. `services.AddScoped<IProcessingQueue, X>()`
é escolhido em runtime entre `PostgresProcessingQueue` e a nova `RabbitMqProcessingQueue`, ambas atrás da
mesma interface, sem alterá-la.

Configurações específicas do RabbitMQ (host, porta, credenciais, nome da fila, prefetch, intervalo do
publicador do outbox) vivem em `RabbitMqOptions`, uma classe nova em `DocReader.Infrastructure.Queue`,
não em `ProcessingQueueOptions`: mantém a fronteira limpa (`ProcessingQueueOptions` continua só sobre o
que os dois provedores compartilham — tentativas, backoff, timeouts — e nada de RabbitMQ vaza para
`Application`) e evita inflar uma classe de opções que outra ADR (0001/0002) já documentou com um
propósito específico.

`apps/api` nunca consome jobs (só `apps/worker` chama `AcquireNextAsync`), mas **grava** jobs: o upload
(`DocumentRepository.AcceptAsync`) e o reprocessamento/reclassificação (`QueueNewAttemptAsync`) rodam
dentro do processo da API, e é exatamente ali que a linha do outbox é gravada — então a API também
precisa saber `QUEUE_PROVIDER`, senão todo documento enviado por ela ficaria silenciosamente sem linha de
outbox, e o publicador do worker nunca veria nada para publicar. **Isto só apareceu na verificação ao
vivo** (a primeira versão do overlay de compose setava `QUEUE_PROVIDER`/`RABBITMQ_*` só no `worker`; um
upload de teste ficou preso em `QUEUED`/`PENDING` para sempre, com `outbox_messages` vazia — o sintoma
certo do bug). A correção: `docker-compose.rabbitmq.yml` seta `DocReader:Queue:Provider` também em `api`,
mas **não** as variáveis de conexão `RABBITMQ_*` — a API nunca abre conexão com o broker (nada em
`apps/api/Program.cs` chama `AddDocReaderRabbitMqConsumer()` nem resolve `IProcessingQueue`), só precisa
acertar a escolha de provedor para `DocumentRepository` decidir gravar a linha do outbox.

### O outbox transacional

`RabbitMqProcessingQueue.EnqueueAsync` existe para honrar o contrato da interface, mas nunca é
chamado pelo caminho real (mesma situação de `PostgresProcessingQueue.CompleteAsync` hoje). O
enfileiramento de verdade continua sendo `DocumentRepository.AcceptAsync` e seu equivalente de
reprocessamento/reclassificação (`QueueNewAttemptAsync`), ambos em `DocReader.Infrastructure`: cada um
agora também grava uma linha em `outbox_messages` (`id`, `aggregate_id` = documentId, `payload` = JSON
com `jobId`/`documentId`, `created_at`, `published_at` nulo, `attempts`), **na mesma
transação/`SaveChangesAsync`** que já grava a linha de `processing_jobs` — um outbox transacional de
verdade, sem two-phase commit, porque é literalmente a mesma transação de banco que já existia. A linha
só é escrita quando `QueueProviderOptions.Provider == RabbitMq`; em modo PostgreSQL `outbox_messages` fica
vazia para sempre e nada a lê.

`RabbitMqOutboxPublisher` (`apps/worker`, hospedado só em modo RabbitMQ) segue exatamente a forma do
`WebhookDispatcher` existente: reivindica linhas não publicadas com `FOR UPDATE SKIP LOCKED`, publica no
broker, marca `published_at`, dorme no intervalo configurado quando não há nada, loga e tenta de novo sem
nunca parar o laço numa falha transitória. É esse laço que prova "broker fora do ar durante o upload,
volta depois": o upload responde `202` normalmente (a transação do documento não depende do broker de
jeito nenhum), a linha do outbox fica com `published_at` nulo enquanto o broker estiver inalcançável, e a
próxima sondagem publica assim que ele voltar — sem nenhuma detecção especial de "saiu do ar", de graça,
herdada da mesma forma de retry-para-sempre do `WebhookDispatcher`.

### A ponte de tempo de vida do DI

`IProcessingQueue` é `Scoped`, e `ProcessingWorker` abre um escopo novo por iteração de job. Se
`RabbitMqProcessingQueue` fosse dona da conexão/canal/consumidor do RabbitMQ, cada job abriria e fecharia
uma conexão e uma assinatura `basic.consume` — errado, caro, e semanticamente quebrado para prefetch=1.
A solução é `RabbitMqJobBridge`, um **singleton** registrado sem `IHostedService` em
`AddDocReaderInfrastructure` (para que `apps/api` também consiga resolver `IProcessingQueue` sem nunca
abrir uma conexão) e promovido a `IHostedService` só em `apps/worker/Program.cs`, via
`AddDocReaderRabbitMqConsumer()`, que reaproveita a mesma instância singleton
(`AddHostedService(sp => sp.GetRequiredService<RabbitMqJobBridge>())`) em vez de criar uma segunda ponte
desconectada da primeira — um erro fácil de cometer com `AddHostedService<T>()` puro, que registra sua
própria instância separada.

A ponte abre uma conexão e dois canais (um para consumir/confirmar, outro para publicar, protegido por um
`SemaphoreSlim` porque o publicador do outbox e o `FailAsync` de um job podem publicar ao mesmo tempo, de
duas hospedagens diferentes), assina a fila principal com o prefetch configurado (padrão 1, o mesmo "um
job por vez por worker" que o laço do `ProcessingWorker` já impõe) e alimenta cada entrega recebida num
`System.Threading.Channels.Channel<T>` limitado à mesma capacidade do prefetch. `RabbitMqProcessingQueue`
(escopada) lê desse canal em `AcquireNextAsync`.

O `deliveryTag` da entrega em processamento fica num campo de instância da própria
`RabbitMqProcessingQueue`, não num dicionário concorrente na ponte: como o `ProcessingWorker` resolve
`IProcessingQueue` uma vez por escopo e o `DocumentProcessor` (que chama `Heartbeat`/`Fail`/`Release`)
recebe essa mesma instância injetada no mesmo escopo, `AcquireNextAsync` e o desfecho do job sempre rodam
sobre o mesmo objeto — um campo de instância é suficiente e mais simples que um mapa global por
`jobId`, sem abrir mão de nenhuma garantia.

### Reserva idempotente: "mensagem repetida não cria segundo job"

O broker já disse exatamente qual job esta entrega é; não há por que reaproveitar o `SELECT ... FOR
UPDATE SKIP LOCKED ... LIMIT 1` do provedor PostgreSQL, pensado para escolher um entre vários candidatos.
`RabbitMqProcessingQueue.AcquireNextAsync` faz um `UPDATE` de uma linha só, mirado por id:

```sql
UPDATE processing_jobs
SET status = 'RUNNING', attempt_count = attempt_count + 1, locked_at = @now, locked_by = @worker,
    started_at = COALESCE(started_at, @now), pages_completed = 0, page_count = NULL
WHERE id = @jobId AND status = 'PENDING';
```

O `WHERE status = 'PENDING'` é a guarda de idempotência: se a mesma mensagem chegar duas vezes (reentrega
depois de uma queda antes do `ack` da primeira aterrissar, por exemplo), a segunda tentativa afeta zero
linhas — o job já está `RUNNING` ou num estado terminal. Isso é tratado como "confirma a duplicata sem
reprocessar" (o `ack` é feito ali mesmo, sem nunca virar um `ProcessingJob` devolvido ao
`ProcessingWorker`), em vez de propagar como um job novo. Testado em `RabbitMqQueueIntegrationTests`:
publicar a mesma mensagem duas vezes resulta numa única execução.

### Heartbeat continua no PostgreSQL, de propósito

`HeartbeatAsync`, a varredura de jobs presos e a contabilidade de retry/falha foram extraídas para
`ProcessingJobBookkeeping`, uma classe interna compartilhada que os dois provedores chamam — para o
heartbeat não poder divergir entre os dois sobre o que significa "ainda dono do job", e para
`pages_completed`/`page_count` (o que `/documents/{id}/status` mostra) funcionar identicamente nos dois
modos. `RabbitMqProcessingQueue` mantém esse heartbeat no PostgreSQL mesmo tendo o RabbitMQ seu próprio
sinal de vivacidade (perda de conexão TCP dispara reentrega automática), porque esse sinal só cobre um
worker que **morre de verdade**. Um worker vivo, conectado, mas travado (laço infinito, deadlock) nunca
derruba a conexão TCP, e o RabbitMQ sozinho nunca notaria. O `x-consumer-timeout` da fila principal (ver
abaixo) é a rede de segurança no nível do broker para esse caso específico; a varredura de jobs presos do
PostgreSQL (reaproveitada, não duplicada) dá o mesmo sinal de obsolescência diagnosticável em banco que já
existe para o modo PostgreSQL. `RabbitMqProcessingQueue.AcquireNextAsync` roda essa mesma varredura antes
de cada aquisição — decisão consciente: sozinha ela não reatribui trabalho no modo RabbitMQ (não existe
mensagem nova até o broker de fato reentregar via `x-consumer-timeout`), mas mantém a linha do job em
PENDING/observável em banco em vez de RUNNING para sempre até o timeout do broker, e faz o
`processing_jobs` da API contar a mesma história nos dois modos.

### Retry via DLX por tentativa, não TTL por mensagem

Um único formato de retry com `expiration` por mensagem foi descartado de propósito: uma fila clássica do
RabbitMQ só expira mensagens **da cabeça** da fila, então uma mensagem de TTL curto atrás de uma de TTL
mais longo não expira na hora — uma pegadinha conhecida do RabbitMQ. Em vez disso, `RabbitMqJobBridge`
declara uma fila de atraso por número de tentativa que ainda pode repetir (`MaxAttempts - 1` filas; com o
padrão `MaxAttempts=3` do compose, são duas: `docreader.processing.jobs.retry.1` e
`docreader.processing.jobs.retry.2`), cada uma com `x-message-ttl` calculado **na declaração**, pela
mesmíssima fórmula que o provedor PostgreSQL já usa
(`DocReader.Application.Processing.RetryBackoff.For`, chamada como função pura, nunca modificada) — com
os padrões do compose isso é 15s depois da tentativa 1 e 30s depois da tentativa 2, não os 10s/30s/90s de
webhook (formato diferente, ilustrativo no pedido original, não o valor real configurado aqui) — e
`x-dead-letter-exchange`/`x-dead-letter-routing-key` apontando de volta para a fila principal **pela
exchange padrão** (nome vazio, que roteia pelo nome da fila): não foi preciso declarar uma segunda
exchange só para isso. `FailAsync`, quando decide repetir, publica na fila de atraso da tentativa que
acabou de falhar; quando é falha definitiva, só confirma a entrega original (a linha já está `FAILED` no
PostgreSQL, não há nada para reentregar).

### `x-consumer-timeout`

A partir do RabbitMQ 3.12, o broker fecha um canal com `PRECONDITION_FAILED` se um consumidor segura uma
entrega sem confirmar por mais que `consumer_timeout`. `ProcessingQueueOptions.ProcessingTimeout` (padrão
60 min) já é o teto absoluto de uma tentativa; `RabbitMqOptions.ConsumerTimeoutMargin` (padrão 5 min) é
somado a ele, e o total vira o argumento `x-consumer-timeout` da fila principal, declarado por
`RabbitMqTopology.ConsumerTimeoutMilliseconds` — calculado a partir das mesmas opções, não uma constante
grande escolhida no escuro. Um documento legitimamente lento (o exemplo medido: 3 páginas, ~34s) nunca é
morto pelo broker no meio do processamento; um consumidor de verdade travado é.

### O gancho de `ack` de sucesso, inteiro em `apps/worker`

`DocumentProcessor.ProcessAsync` (Application, inalterado) nunca chama `IProcessingQueue.CompleteAsync`
no caminho de sucesso — a escrita real de conclusão é `IDocumentProcessingStore.CompleteAsync`, já
correta e alheia a esta ADR. `ProcessingWorker.ProcessNextAsync` (`apps/worker`) é o gancho: depois que
`processor.ProcessAsync(job, ct)` retorna sem lançar, chama `queue.CompleteAsync(job, ct)` — só em modo
RabbitMQ, para não introduzir nenhum caminho de código novo em modo PostgreSQL (chamar
`PostgresProcessingQueue.CompleteAsync` ali seria inofensivo na maioria das vezes, mas logaria um aviso
confuso de "Complete refused" sempre que o job já estivesse `COMPLETED`, o que é sempre). Como
`ProcessAsync` nunca relança (toda falha interna, incluindo perda de posse do job, é tratada e o método
retorna normalmente), `RabbitMqProcessingQueue.CompleteAsync` é chamado incondicionalmente depois de
**todo** retorno — e é seguro porque é um no-op quando `FailAsync`/`ReleaseAsync` já resolveram a entrega
antes, dentro do próprio `ProcessAsync` (o campo de instância do `deliveryTag` já foi limpo).

## Alternativas consideradas

| Alternativa | Por que não |
|---|---|
| TTL por mensagem (`expiration`) numa única fila de retry | RabbitMQ só expira da cabeça da fila: uma mensagem de retentativa curta atrás de uma longa não sai na hora. Pegadinha documentada do RabbitMQ, evitada com uma fila de atraso por tentativa. |
| `RabbitMqProcessingQueue` dona da conexão | Escopada, recriaria conexão/consumidor a cada job (o `ProcessingWorker` abre um escopo por iteração). Resolvido com uma ponte singleton hospedada só em `apps/worker`. |
| Adicionar `Provider` a `ProcessingQueueOptions` (`DocReader.Application`) | Seria uma mudança, ainda que pequena, na camada proibida. `QueueProviderOptions`/`RabbitMqOptions` inteiros em `DocReader.Infrastructure` evitam a questão. |
| Redeclarar o heartbeat/retry em SQL para o RabbitMQ | Duplicaria a contabilidade do PostgreSQL e arriscaria os dois provedores divergirem sobre posse do job. Extraído para `ProcessingJobBookkeeping`, compartilhado. |
| Confiar só na reentrega automática do RabbitMQ para worker travado | Só cobre conexão derrubada, não um consumidor vivo e travado. `x-consumer-timeout` cobre esse caso no broker; a varredura do PostgreSQL mantém o mesmo sinal diagnosticável dos dois modos. |

## Consequências

**Positivas**

- PostgreSQL continua o padrão sem nenhuma mudança de comportamento (build e suíte de testes idênticos
  em contagem antes/depois desta ADR); RabbitMQ é puramente opt-in.
- Fanout/roteamento de verdade, familiaridade operacional com um broker padrão de mercado, UI de
  management para inspeção manual - exatamente o que a ADR 0001 registrou como o que faltaria.
- O outbox transacional preserva a garantia "documento aceito nunca fica sem job" mesmo sem a
  transação única que a ADR 0001 tinha: a linha do outbox nasce na mesma transação da linha do job.
- Retry por fila de atraso reproduz o mesmo backoff exponencial do PostgreSQL, calculado pela mesma
  função, então o comportamento observável (quanto tempo até a próxima tentativa) é idêntico nos dois
  modos.

**Negativas e limites**

- Um componente a mais para operar, monitorar e fazer backup - o próprio ponto que a ADR 0001 citou
  contra o RabbitMQ na Etapa 1. Aceitável agora porque é opcional, não porque deixou de ser verdade.
- Perda da inserção transacional direta (`EnqueueAsync` chamando o broker) é compensada pelo outbox, mas
  isso é uma sondagem periódica, não instantânea: `RabbitMqOptions.OutboxPollInterval` (padrão 2s) é o
  atraso extra entre "documento aceito" e "mensagem no broker", que não existe no modo PostgreSQL.
- A varredura de jobs presos, em modo RabbitMQ, não reatribui trabalho sozinha (só o `x-consumer-timeout`
  do broker o faz): ela mantém o banco honesto, mas um leitor que espera "sweep resolve tudo" como no
  modo PostgreSQL vai se enganar. Documentado aqui de propósito.
- Duas filas de infraestrutura de retenção de estado (`processing_jobs` no PostgreSQL e as filas do
  RabbitMQ) para o mesmo trabalho, o "duas fontes de verdade" que a ADR 0001 já havia citado contra o
  RabbitMQ. Mitigado pelo `WHERE status = 'PENDING'` como única fonte de decisão de posse — o RabbitMQ
  nunca decide sozinho se um job é novo, só entrega candidatos.

## Gatilhos de revisão

Reabrir esta decisão se: o outbox acumular atraso relevante (sinal de que o polling não basta, e
`LISTEN`/`NOTIFY` ou um outbox pattern com push mereceria entrar); alguém precisar de fanout de verdade
(hoje a fila principal é ponto a ponto, sem exchange própria); ou a varredura de jobs presos em modo
RabbitMQ se mostrar insuficiente antes do `x-consumer-timeout` disparar, sinal de que o intervalo entre
sondagens (que é o mesmo `JobLockTimeout` do PostgreSQL) precisa de um número diferente por provedor.
