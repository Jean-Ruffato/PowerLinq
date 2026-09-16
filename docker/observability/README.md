# Stack de observabilidade

Recebe e mostra a telemetria que a biblioteca emite. Um comando:

```bash
docker compose -f docker/observability/compose.yaml up -d
```

Grafana em <http://localhost:3000>, já com os datasources ligados e o dashboard do PowerLinq
como página inicial. Sem login e sem passo manual na UI.

Com **Podman**, `podman compose` funciona igual — foi assim que esta stack foi verificada:

```bash
podman compose -f docker/observability/compose.yaml up -d
```

Depois, aponte o exportador OTLP da sua aplicação para os dois destinos e gere tráfego. Em
poucos segundos os painéis começam a preencher.

| Sinal   | Destino                                        |
| ------- | ---------------------------------------------- |
| Trace   | `http://localhost:4318/v1/traces`              |
| Métrica | `http://localhost:9090/api/v1/otlp/v1/metrics` |

A fiação do OpenTelemetry é decisão da aplicação: a biblioteca não depende de pacote nenhum
do OTel, só expõe `PowerLinqDiagnostics.ActivitySourceName` e `PowerLinqDiagnostics.MeterName`
para registrar como source e como meter.

> **Os dois destinos são diferentes, e isso importa.** Métrica vai para o Prometheus (receptor
> OTLP) e trace para o Tempo. Usar a variável `OTEL_EXPORTER_OTLP_ENDPOINT` para os dois
> **não funciona**: o Tempo devolve `404` para métrica. E as variáveis por sinal
> (`OTEL_EXPORTER_OTLP_METRICS_ENDPOINT`) não se mostraram confiáveis — medido, o exportador
> continuou mandando métrica para o padrão `localhost:4318`, e os traces "funcionavam" por
> coincidência, já que aquele padrão é exatamente a porta do Tempo. Por isso vale configurar
> um endpoint por sinal, explicitamente, em vez de confiar na variável única.

Derrubar tudo:

```bash
docker compose -f docker/observability/compose.yaml down -v
```

## Por que Grafana, e não Kibana

A escolha vem da forma do que é emitido, não de preferência:

- **O que a biblioteca emite é métrica dimensional**, não log. `Counter`, `Histogram` e
  `ObservableGauge` têm o Prometheus como consumidor canônico, e o painel mais importante —
  saturação do pool — é um `histogram_quantile`.
- **O único conteúdo de log é o DAX e a linha de consulta lenta.** Subir um Elasticsearch
  para isso seria o componente mais pesado do compose para o menor uso.
- **Dashboard como código é nativo aqui.** Datasource em YAML e dashboard em JSON montados
  como volume: declarativo e versionado. Importar saved object por API seria passo
  imperativo depois de o serviço subir, e passo imperativo em setup é o que quebra em
  silêncio.
- **Correlação entre os sinais.** O caso de uso real é *"esta consulta demorou 8 s — qual
  DAX era, e de qual tenant?"*. Exemplar liga métrica a trace, e o trace carrega o DAX.

Se você quer só olhar rápido e não precisa de dashboard próprio nem de retenção, o
**dashboard do .NET Aspire** é um contêiner único que aceita OTLP e mostra os três sinais
sem configuração. Serve para validar que o exportador está certo antes de investir aqui.

## O que cada painel responde

| painel | pergunta |
|---|---|
| Taxa de erro | as consultas estão falhando? |
| Consultas por segundo | qual o volume, e quanto dele falha? |
| Latência p50/p95/p99 | está lento? |
| **p95 por workspace** | é um cliente, ou é o serviço? |
| Erros por workspace | a falha está concentrada num destino? |
| **Espera pelo pool (p95)** | o teto de conexões está apertando? |
| **Reúso de conexão** | o pool está entregando o que justifica existir? |
| Conexões vivas e ociosas | qual o estado do pool agora? |
| Repetições | quantas sessões estão morrendo? |
| Traces de consulta | qual DAX causou aquele ponto lento? |

Os três em negrito são os que não existiriam sem decisões tomadas na instrumentação, e
valem explicação:

**p95 por workspace** é o painel que dá sentido a carregar workspace e dataset em toda
métrica. Sem essa separação, um modelo lento de um cliente aparece como piora do serviço
inteiro — e a ação correta para os dois casos é oposta.

**Espera pelo pool** é indicador **antecedente**. Com folga fica perto de zero; quando
`MaxConnectionsPerModel` aperta, ela cresce *antes* de qualquer erro. Sem ela, saturação se
manifesta apenas como latência inexplicada na consulta.

**Reúso de conexão** verifica a premissa do próprio pool. Se `reused / (reused + opened)`
está baixo, o pool não está reaproveitando nada e o custo de tê-lo não se paga.

## Detalhes que fazem os painéis funcionarem

**Fronteiras de histograma explícitas**, definidas por `AddView` na aplicação. As padrão do
OTel não servem aos dois casos: a espera no pool é sub-milissegundo quando há folga, e com
os buckets padrão todo o sinal cai no primeiro deles — o p95 fica inútil justamente no
painel de saturação. A duração de consulta, ao contrário, precisa de resolução até dezenas
de segundos.

**`service.instance.id`** como atributo de recurso. Os gauges de conexão são **por
processo**: sem separar por instância, duas réplicas aparecem como uma serra sem
explicação.

**Nomes traduzidos no Prometheus.** Métrica vinda por OTLP tem os pontos convertidos em
sublinhados e a unidade anexada, então `powerlinq.query.duration` chega como
`powerlinq_query_duration_milliseconds`. A tradução está configurada no
`prometheus.yml`, e as consultas do dashboard já usam a forma final.

## Os dois painéis do Tempo rodam no navegador

O painel de **consultas lentas** e o de **mapa de serviços** usam o datasource do Tempo, e os
dois são executados pelo **navegador**. O backend do Grafana recusa os dois explicitamente:

```
backend TraceQL search queries are not supported
unsupported query type: 'serviceMap'
```

Consequências práticas:

- **não dá para verificá-los por API**, nem com `/api/ds/query`. Os painéis de métrica, sim;
- **não servem de base para alerta**, que roda no servidor;
- se abrirem vazios, a checagem é no console do navegador, não no log do Grafana.

Para conferir a consulta em si sem depender do navegador, a API do Tempo aceita direto:

```bash
curl -sG http://localhost:3200/api/search   --data-urlencode 'q={ name = "powerlinq.query" }'
```

Um detalhe que já custou um painel vazio: no `select()` do TraceQL os atributos **precisam de
prefixo de escopo**. `select(powerlinq.dataset)` é erro de sintaxe e a busca volta `400`;
`select(span.powerlinq.dataset)` funciona.

O caminho "do gráfico para o DAX" **não** depende desses dois painéis: o exemplar no painel de
latência leva ao trace, e isso é servido pelo Prometheus.

## Dado sensível

**`RecordDaxInTelemetry` fica `false` em qualquer stack compartilhada apontada para dado
real.** O DAX carrega os *valores* dos filtros — CNPJ, identificador de cliente, nome — e o
span vai para um backend com retenção e controle de acesso próprios, diferentes dos do
banco.

Ligá-lo se justifica no caso oposto: stack local, apontada para dado do próprio time.

O rótulo de destino **não** carrega segredo: a connection string do pool tem só
`Data Source` e `Catalog`, porque a autenticação é por token aplicado na conexão.

## Cardinalidade

Workspace e dataset como rótulo significam uma série por destino. Para dezenas de
workspaces isso é confortável; para milhares, não. Se o número de tenants crescer, as saídas
são agregar por algo mais grosso (região, plano) ou manter o rótulo apenas nos traces, onde
cardinalidade alta é esperada. **Decidir antes de chegar lá**, não depois de o Prometheus
começar a consumir memória.

## Isto é para desenvolvimento

Sem autenticação no Grafana, sem persistência configurada, sem limite de retenção. Em
produção, o desenho normalmente ganha um **OpenTelemetry Collector** na frente — para
agregar, fazer buffer e desacoplar a aplicação do backend. Aqui ele foi omitido de
propósito: seria um contêiner e um arquivo a mais para reencaminhar o que os dois destinos
já aceitam direto.
