# ADR 0001 — PostgreSQL como fila de processamento (FOR UPDATE SKIP LOCKED)

- **Status:** Aceita
- **Data:** 2026-09-23
- **Contexto do plano:** Etapa 1 — walking skeleton
- **Relacionada a:** PRD §10 (PostgreSQL como banco e fila), RF-007, §20 Resiliência

## Contexto

O upload não pode esperar o OCR: a API precisa responder `202 Accepted` com protocolo em até
2 segundos, e o OCR de um documento de cinco páginas pode levar dezenas de segundos em CPU.
Existe, portanto, um trabalho assíncrono entre `api` e `worker` que precisa de:

- persistência, para que reinício de contêiner não perca job aceito (critério de aceite 10);
- reserva exclusiva, para que dois workers não processem o mesmo documento (RF-013);
- reentrega de job preso após timeout, com até três tentativas e backoff (RF-007);
- substituição futura por um broker gerenciado, sem reescrever domínio nem API pública.

A PoC roda em uma máquina, precisa subir com um único `docker compose up` e já carrega quatro
serviços de aplicação mais o banco. PostgreSQL é obrigatório de qualquer forma, porque é onde
ficam documentos, extrações e eventos.

## Decisão

A fila da PoC será a tabela `processing_jobs` no próprio PostgreSQL. O worker reserva trabalho
com `SELECT ... FOR UPDATE SKIP LOCKED`, dentro de uma transação:

```sql
SELECT id
FROM processing_jobs
WHERE status = 'PENDING' AND available_at <= now()
ORDER BY created_at
FOR UPDATE SKIP LOCKED
LIMIT 1;
```

O consumo fica atrás do contrato `IProcessingQueue` (`EnqueueAsync`, `AcquireNextAsync`,
`CompleteAsync`, `FailAsync`). A implementação `PostgresProcessingQueue` vive em
`DocReader.Infrastructure`; nem `Domain` nem `Application` conhecem SQL ou EF Core.

Regras que acompanham a decisão:

1. **Enfileiramento transacional.** O documento e seu job são gravados na mesma transação do
   upload. Não existe documento aceito sem job, nem job sem documento.
2. **Reserva com lock de linha.** `FOR UPDATE SKIP LOCKED` faz cada worker pular linhas já
   travadas em vez de bloquear, o que permite escalar réplicas de worker sem coordenação
   externa. A linha reservada recebe `status = 'RUNNING'`, `locked_at` e `locked_by`.
3. **Recuperação por timeout.** Um job `RUNNING` com `locked_at` mais antigo que
   `JobLockTimeout` volta para `PENDING` com `attempt_count` incrementado. É isso que cobre
   worker morto no meio do processamento.
4. **Retry com backoff.** Falha transitória agenda nova tentativa por `available_at = now() +
   backoff(attempt_count)`. Esgotadas três tentativas, o job vai para `FAILED` e o documento
   registra `last_error_code`.
5. **Idempotência do consumo.** O processamento é escrito para poder repetir: reprocessar cria
   nova tentativa e nova extração, sem apagar a anterior, e nunca marca resultado parcial
   como `COMPLETED`.
6. **Sem polling agressivo.** O worker faz poll curto com intervalo configurável. Se o custo
   incomodar, o passo seguinte é `LISTEN`/`NOTIFY` na mesma tabela, ainda sem broker.

## Alternativas consideradas

| Alternativa | Por que não agora |
|---|---|
| RabbitMQ | Melhor fila de verdade, mas adiciona sexto contêiner, credenciais, UI de management e uma segunda fonte de verdade para estado de job. A PoC teria dois lugares para consultar o que aconteceu com um documento. |
| Kafka | Dimensionado para throughput e retenção de eventos que a PoC não tem. Custo operacional e de memória desproporcional para uma máquina local. |
| Redis (listas/streams) | Leve, porém sem durabilidade transacional junto do documento. Um `202` respondido e um Redis reiniciado produziriam documento aceito sem job, violando o critério de aceite 10. |
| Fila em memória no worker | Perde job em qualquer reinício e impede mais de uma réplica. Contraria RF-007 diretamente. |
| Timer varrendo `documents` por status | Sem reserva exclusiva; duas réplicas processariam o mesmo documento. Seria reinventar o lock, pior. |

## Consequências

**Positivas**

- Um único componente com estado na PoC; backup, inspeção e teste de integração em um só lugar.
- Enfileiramento e persistência do documento na mesma transação, sem entrega duplicada nem
  perdida por falha entre dois sistemas.
- Fila inspecionável por SQL, o que ajuda a demonstração e o diagnóstico.
- Retry, backoff, tentativa e erro ficam visíveis como colunas, não como estado opaco do broker.

**Negativas e limites**

- O banco acumula carga de fila além da carga de consulta. Aceitável na PoC; é risco registrado
  no PRD §27 ("Banco virar gargalo").
- Poll periódico gera consultas ociosas. Mitigado por intervalo configurável e,
  se necessário, `LISTEN`/`NOTIFY`.
- Não há fanout, roteamento por tópico, dead-letter nativo nem priorização sofisticada. Se algum
  desses aparecer como requisito, é sinal de trocar a implementação.
- Longas transações de reserva travariam linhas: a transação de `AcquireNextAsync` cobre apenas
  a reserva, nunca o processamento inteiro.

## Gatilhos de revisão

Reabrir esta decisão quando ocorrer qualquer um destes: mais de um processo consumidor
competindo em escala real, necessidade de dead-letter ou fanout, tempo de espera dominado por
contenção no banco, ou promoção da PoC a ambiente produtivo com SLA. A troca deve custar uma
nova implementação de `IProcessingQueue` e nenhuma mudança em `Domain`, `Application` ou na
API pública.
