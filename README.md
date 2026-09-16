# PowerLinq

Provider LINQ para DAX. Traduz expressões C# em consultas DAX e materializa o
resultado em objetos, contra endpoints XMLA (Power BI Premium / Analysis Services).

```csharp
var produtos = await tabela
    .Where(p => p.Categoria == "Eletrônicos" && p.Preco > 100.5m)
    .OrderByDescending(p => p.Preco)
    .Take(5)
    .ToListAsync();
```

Gera:

```dax
EVALUATE
TOPN(
    5,
    FILTER(
        Produto,
        Produto[Categoria] = "Eletrônicos" && Produto[Preco] > 100.5
    ),
    Produto[Preco], DESC
)
```

> **Estado do projeto:** em desenvolvimento, sem release publicado. Há **duas**
> superfícies de consulta sobre o mesmo motor: a API fluente acima, onde o que o
> DAX não expressa não compila, e [`IQueryable`](#linq-de-verdade-iqueryable),
> que dá sintaxe de query e interoperabilidade em troca dessa garantia. Ver
> [Limitações](#limitações) antes de adotar.

## Requisitos

- .NET 10 (`net10.0`)
- Um endpoint XMLA, para execução real

## Como usar

### 1. Mapeie a entidade

Os atributos são opcionais. Sem eles, o nome do tipo vira o nome da tabela e o
nome da propriedade vira `Tabela[Propriedade]`.

Nome com espaço ou caractere especial é aspado automaticamente, como o DAX exige
— `[DaxTable("Ordem de Venda")]` gera `'Ordem de Venda'`. Aspa simples dentro do
nome é escapada por duplicação, e um nome já aspado no atributo é preservado.

Em `[DaxColumn]`, **as duas grafias da referência funcionam**: `'Tabela'[Coluna]` e
`Tabela[Coluna]` mapeiam a mesma coluna. A distinção importa porque a string do atributo
serve a duas coisas — vira o texto da referência no DAX **e** é a chave usada para achar a
coluna no resultado — e o servidor devolve o nome com a tabela sem aspas, enquanto a
convenção do DAX é com. As duas são registradas, e o DAX gerado continua saindo na grafia
que você escreveu.

```csharp
using PowerLinq.DaxConverter.Attributes;

[DaxTable("Produto")]
public sealed class Produto
{
    [DaxColumn("Produto[ProdutoID]")]
    public int ProdutoId { get; set; }

    [DaxColumn("Produto[Nome]")]
    public string Nome { get; set; } = "";

    [DaxColumn("Produto[Categoria]")]
    public string Categoria { get; set; } = "";

    [DaxColumn("Produto[Preco]")]
    public decimal Preco { get; set; }

    [DaxColumn("Produto[Ativo]")]
    public bool Ativo { get; set; }
}
```

Tipos que a materialização converte, além da forma anulável de cada um:

| Categoria | Tipos |
|---|---|
| Numéricos | `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal` |
| Texto e lógico | `string`, `char`, `bool` |
| Enumeração | qualquer `enum`, pelo valor numérico ou pelo nome |
| Data e hora | `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan` |
| Identificador | `Guid` |

Um tipo fora dessa lista lança `NotSupportedException` nomeando a propriedade, a
coluna e o tipo — em vez do `InvalidCastException` genérico de antes.

#### Contratos imutáveis

O contrato não precisa ter propriedade gravável. `record` posicional, tipo anônimo e
DTO de construtor materializam pelo construtor:

```csharp
public sealed record ProdutoResumo(
    [property: DaxColumn("Produto[Nome]")] string Nome,
    [property: DaxColumn("Produto[Preco]")] decimal Preco);

var resumo = await contexto.Produtos
    .Where(p => p.Ativo)
    .Select(p => new ProdutoResumo(p.Nome, p.Preco))
    .ToListAsync();

var anonimo = await contexto.Produtos
    .Select(p => new { Codigo = p.ProdutoId, p.Nome })
    .ToListAsync();
```

**A regra é o construtor sem parâmetros ganhar.** Quando o tipo tem um, a materialização
atribui propriedades — o caminho que sempre existiu, então nenhum contrato atual muda de
comportamento. Só na ausência dele o construtor com parâmetros entra, e o de maior aridade
vence; empate de aridade é recusado com `NotSupportedException`, porque escolher em
silêncio faria a materialização depender da ordem em que a reflexão devolve os construtores.

A coluna de cada parâmetro sai da propriedade de mesmo nome quando ela existe — num `record`
posicional ela é gerada junto e pode carregar `[DaxColumn]`, e é o que faz o mesmo tipo mapear
igual pelos dois caminhos. Sem propriedade correspondente, vale `[DaxColumn]` no próprio
parâmetro; sem nada, valem o nome e o nome entre colchetes. Coluna ausente no resultado deixa
o parâmetro no `default` do tipo, a mesma semântica do caminho de propriedades, onde a
propriedade simplesmente não é atribuída.

### 2. Crie um contexto

```csharp
using PowerLinq.DaxConverter.Context;
using PowerLinq.DaxConverter.Execution;

public sealed class CatalogoContext(
    IDaxQueryExecutor executor,
    IDaxTableFactory tableFactory) : DaxContext(executor, tableFactory)
{
    public IDaxTable<Produto> Produtos => Set<Produto>();
}
```

Registre o provedor, o executor e o contexto no contêiner:

```csharp
using PowerLinq.Extensions.DependencyInjection;

builder.Services.AddXmlaConnectionPool(builder.Configuration);
builder.Services.AddScoped<IDaxQueryExecutor, PooledXmlaQueryExecutor>();
builder.Services.AddDaxContext<CatalogoContext>(builder.Configuration);
```

O idioma das mensagens é opcional. O padrão e o fallback são inglês:

```json
{
  "PowerLinq": {
    "Localization": {
      "Language": "pt-BR"
    }
  }
}
```

São aceitos `en`, `pt` e `pt-BR`. Qualquer outro valor usa `en`.

Agora os serviços dependem do contexto ou de `IDaxTable<T>`, sem instanciar o
executor ou a tabela:

```csharp
public sealed class ProdutoService(CatalogoContext context)
{
    private readonly IDaxTable<Produto> _produtos = context.Produtos;

    public Task<List<Produto>> ListarAsync() => _produtos.ToListAsync();
}
```

### 2b. Multi-tenant: destino resolvido em execução

Quando o mesmo dataset é publicado num workspace **por cliente**, o destino só se
conhece no meio da requisição — depois de resolver o tenant. Nesse caso, injete
`IDaxContextFactory` e crie o contexto para o destino:

```csharp
public sealed class RelatorioService(IDaxContextFactory contexts, ITenantResolver tenants)
{
    public async Task<List<Produto>> ListarAsync(string clienteId)
    {
        DaxTarget destino = await tenants.ResolveAsync(clienteId);

        CatalogoContext context = contexts.Create<CatalogoContext>(destino);

        return await context.Produtos.Where(p => p.Ativo).ToListAsync();
    }
}
```

`DaxTarget` recebe o **endpoint XMLA completo** do workspace e o nome do dataset:

```csharp
var destino = new DaxTarget(
    "powerbi://api.powerbi.com/v1.0/myorg/ClienteA",
    "ModeloVendas");
```

É o endpoint inteiro, e não um nome de workspace a ser composto numa URI, porque a
composição dependeria da nuvem (comercial, governamental, soberana) e de convenção
de tenant — errar isso daria uma falha de autenticação difícil de ler.

Três coisas que valem saber:

- **As conexões continuam agrupadas por destino.** A connection string derivada do
  `DaxTarget` é a chave do pool, então criar um contexto por requisição não abre uma
  conexão por consulta: duas requisições para o mesmo cliente alugam do mesmo grupo.
- **Destino incompleto falha na criação**, não na consulta, com mensagem citando os
  dois campos — `workspace='', dataset='Modelo'`. O stack trace aponta para onde o
  tenant foi resolvido, que é onde o problema está.
- **Aplicação de destino único não muda nada.** Sem `DaxTarget`, o executor usa
  `XmlaEndpoint` e `Dataset` do appsettings, como sempre; o contexto resolvido do
  contêiner segue funcionando.

O contexto vindo da fábrica é uma **instância nova**, com executor próprio — dois
destinos não podem compartilhar contexto, porque é o executor que carrega o destino.

### 2c. Health check do endpoint

```csharp
builder.Services.AddHealthChecks().AddPowerLinqXmlaCheck(tags: "ready");

app.MapHealthChecks("/health");
```

A sondagem é `EVALUATE ROW("ping", 1)`. Um `ROW` com literal não referencia tabela
nenhuma, então funciona em **qualquer** dataset — o check não precisa saber o schema
do modelo e não quebra quando o modelo muda. O que ele prova é o que um readiness
probe precisa saber: endpoint alcançável, autenticação válida e XMLA habilitado no
workspace.

São **três** estados, não dois, porque pedem ações diferentes:

| estado | quando | o que fazer |
|---|---|---|
| `Healthy` | a sondagem respondeu | — |
| `Degraded` | não configurado | erro de implantação: falta `PowerBi:XmlaEndpoint`/`Dataset` |
| `Degraded` | estourou o timeout do check | saturação do pool ou lentidão do servidor |
| `Unhealthy` | falha de conexão ou autenticação | incidente |

Saturação não vira `Unhealthy` de propósito: tirar o pod de rotação justamente quando
ele está atendendo tráfego piora o problema em vez de resolver.

O timeout é **próprio**, `PowerBi:HealthCheckTimeoutSeconds` (padrão 10s), e não o de
consulta. Um probe que espera 120 s não é um probe — o orquestrador já desistiu antes.
Esse teto também limita por quanto tempo o check pode ocupar uma das conexões do pool,
cujo padrão é 4 por modelo.

O resultado carrega diagnóstico suficiente para agir — endpoint, dataset, se há service
principal, se o pool está ligado — e **nunca** o segredo.

### 2d. Observabilidade

A biblioteca emite um `ActivitySource` e um `Meter` nos nomes que o OpenTelemetry
consome direto:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(PowerLinqDiagnostics.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(PowerLinqDiagnostics.MeterName));
```

Os nomes são constantes públicas de propósito — um literal solto em `AddSource`
quebra em silêncio quando o nome muda.

**Span** `powerlinq.query` por execução, com duração, workspace e dataset.

**Métricas:**

| instrumento | o que mede |
|---|---|
| `powerlinq.queries` | consultas, com `outcome` = `ok`/`error` |
| `powerlinq.query.duration` | duração da execução (ms) |
| `powerlinq.query.retries` | repetições por sessão quebrada |
| `powerlinq.pool.wait` | espera por uma conexão do pool (ms) |
| `powerlinq.pool.rentals` | empréstimos, com `origin` = `reused`/`opened` |
| `powerlinq.pool.connections.live` / `.idle` | estado do pool |

`powerlinq.pool.wait` é **o** sinal de saturação de `MaxConnectionsPerModel`: com
folga fica perto de zero, e quando o teto aperta ela cresce antes de qualquer erro
aparecer. Sem ela, saturação se manifesta só como latência inexplicada.

Toda métrica e todo span levam **workspace e dataset** como dimensão. Num processo
multi-tenant isso é o que separa "um cliente com modelo lento" de "degradação geral".

Para **ver** isso, há uma stack pronta em [docker/observability](docker/observability/README.md):

```bash
docker compose -f docker/observability/compose.yaml up -d
```

Grafana em <http://localhost:3000>, já com datasources e dashboard provisionados — sem passo
manual na UI. Grafana e não Kibana porque o que a biblioteca emite é métrica dimensional, e
o painel mais importante (saturação do pool) é um `histogram_quantile`.

**O texto do DAX é opt-in:**

```json
{ "PowerBi": { "RecordDaxInTelemetry": true, "SlowQueryLogThresholdMs": 1000 } }
```

`RecordDaxInTelemetry` é `false` por padrão porque **o DAX carrega os valores dos
filtros** — CNPJ, identificador de cliente, nome — e o span vai para um backend de
tracing com retenção e controle de acesso próprios, diferentes dos do banco. Ligar
isso é decisão de quem conhece o dado.

No log, o DAX sai sempre em `Debug`; acima de `SlowQueryLogThresholdMs` a execução
aparece em `Information` com a duração, para que consulta lenta seja visível em
produção sem inundar o log com as rápidas. `0` desliga.

### 3. Consulte

```csharp
var eletronicos = await context.Produtos
    .Where(p => p.Categoria == "Eletrônicos")
    .OrderBy(p => p.Nome)
    .ToListAsync();

var maisCaro = await context.Produtos.OrderByDescending(p => p.Preco).FirstOrDefaultAsync();

var total = await context.Produtos.Where(p => p.Ativo).CountAsync();
```

Os operadores existem tanto em `IDaxTable<T>` quanto em `DaxQuery<T>`, o retorno
de `Where` — encadear a partir da tabela ou de um filtro dá no mesmo.

Para inspecionar o DAX sem executar:

```csharp
Console.WriteLine(context.Produtos.Where(p => p.Ativo).ToDaxString());
```

### LINQ de verdade: `IQueryable`

`AsQueryable()` devolve um `IQueryable<T>` sobre a **mesma** consulta, e com ele valem
a sintaxe de query e qualquer código que receba `IQueryable<T>`:

```csharp
using PowerLinq.DaxConverter.Linq;

IQueryable<Produto> produtos = context.Produtos.AsQueryable();

var caros =
    from produto in produtos
    where produto.Ativo && produto.Preco > 100m
    orderby produto.Preco descending
    select new ProdutoResumo { Nome = produto.Nome, Preco = produto.Preco };

List<ProdutoResumo> resultado = await caros.Take(10).ToListAsync();
```

O `ToListAsync` daí é o mesmo da API fluente: a consulta atravessa como **pipeline**, e não
como DAX pronto. Uma consulta já composta pela fluente também atravessa na outra direção —
filtrar com a API que erra em tempo de compilação e só então entregar o resultado a quem
espera `IQueryable`:

```csharp
IQueryable<Produto> ativos = context.Produtos.Where(p => p.Ativo).AsQueryable();

// Uma função que não conhece DAX nem esta biblioteca.
static IQueryable<T> Pagina<T>(IQueryable<T> fonte, int pagina, int tamanho) =>
    fonte.Skip((pagina - 1) * tamanho).Take(tamanho);

var terceira = await Pagina(ativos.OrderBy(p => p.Id), 3, 20).ToListAsync();
```

**As duas superfícies convivem, e a escolha tem consequência.** Na fluente, o que o DAX
não expressa não compila. Com `IQueryable`, `.Where(x => Regex.IsMatch(x.Nome, p))` compila
perfeitamente e só é recusado na tradução — o preço da sintaxe de query e da
interoperabilidade. A decisão foi manter as duas superfícies, e não substituir uma pela
outra.

**A recusa acontece na composição, não na execução.** O operador sem tradução estoura na
linha em que foi escrito, sem esperar o terminal:

```csharp
produtos.Where(p => Regex.IsMatch(p.Nome, "^A"));   // NotSupportedException aqui
```

**Não há enumeração síncrona.** `foreach` e `ToList()` lançam nomeando o terminal
assíncrono equivalente, e o mesmo vale para `Count()`, `Any()` e companhia:

```csharp
foreach (var p in produtos) { }     // NotSupportedException: use ToListAsync/AsAsyncEnumerable
int n = produtos.Count();           // NotSupportedException: use CountAsync
```

Não é limitação de implementação, é a mesma decisão do resto da biblioteca: cada terminal é
uma ida ao endpoint XMLA, e servir um `foreach` exigiria bloquear a thread esperando a rede.
Sob carga isso esgota o pool de threads, com o sintoma aparecendo longe da causa.

**Nada cai em avaliação no cliente.** Nenhum operador é traduzido pela metade e completado
em memória — é o erro que o EF Core 3.0 removeu, e ali a tabela que vinha inteira não era um
fato de milhões de linhas. A fronteira para seguir em memória é explícita e tem nome:
`ToListAsync` materializa, `AsAsyncEnumerable` entrega linha a linha, e depois dela vale o
LINQ a objetos.

Operadores traduzidos: `Where`, `OrderBy`, `OrderByDescending`, `ThenBy`,
`ThenByDescending`, `Skip`, `Take`, `Distinct` e `Select`. Os terminais são os mesmos da
fluente, com o sufixo `Async` — incluindo `ToPagedListAsync`, `AsAsyncEnumerable` e
`MeasureAsync`. `GroupBy`, `Join` e os agregados de grupo ficam na API fluente: a forma
deles é específica do DAX — `IDaxGroup`, coluna de extensão, medida que não é iterada — e
`IGrouping` não a expressa.

## Operadores suportados

| Método | DAX gerado |
|---|---|
| `Where` | `FILTER(...)`, combinados com `&&` quando encadeados |
| `WhereIf` | ignora o predicado se a condição for falsa ou o texto vazio |
| `OrderBy` / `OrderByDescending` | `ORDER BY ... ASC/DESC` — aceita **expressão**, não só propriedade |
| `ThenBy` / `ThenByDescending` | termos adicionais de ordenação |
| `Take` | `TOPN(n, ...)` — a ordenação passa a ser argumento do `TOPN` |
| `Skip` | `EXCEPT(fonte, TOPN(n, fonte, ...))` — requer ordenação; a fonte é declarada em `VAR` quando não é tabela crua |
| `Select` | `SELECTCOLUMNS(...)` — projeta diretamente para DTOs/contratos |
| `GroupBy` | `SUMMARIZECOLUMNS(...)`, via `Select` no grupo. `OrderBy` antes dele é honrado sobre coluna de chave |
| `WithSubtotal()` | `ROLLUPADDISSUBTOTAL(ROLLUPGROUP(<chaves>), "is_total")` — linha de total junto das de detalhe |
| `WithSubtotal(nome, chaves)` | acrescenta um nível a `ROLLUPADDISSUBTOTAL` — hierarquia de dois ou mais níveis, cada um com a própria flag |
| `Aggregate` | colunas de extensão sobre a tabela |
| `ToListAsync` | `EVALUATE` |
| `ToPagedListAsync` | `EVALUATE ADDCOLUMNS(<página>, "[__pl_total]", <total>)` — página **e** total numa consulta |
| `AsAsyncEnumerable` | `EVALUATE` — mesma consulta, linhas entregues conforme chegam |
| `ToArrayAsync` | `EVALUATE` — mesma consulta, resultado como vetor |
| `ToDictionaryAsync` | `EVALUATE` — os seletores de chave e valor rodam **no cliente** |
| `FirstAsync` / `FirstOrDefaultAsync` | `EVALUATE` com `TOPN(1, ...)` |
| `SingleAsync` / `SingleOrDefaultAsync` | `EVALUATE` com `TOPN(2, ...)` — a segunda linha existe só para poder recusá-la |
| `CountAsync` | `EVALUATE ROW("[Count]", COUNTROWS(...))` |
| `LongCountAsync` | o mesmo DAX, lido como `long` |
| `SumAsync` | `EVALUATE ROW("[Value]", SUMX(<fonte filtrada>, <expressão>))` |
| `MinAsync` / `MaxAsync` | idem, com `MINX` / `MAXX` |
| `AverageAsync` / `AverageOrDefaultAsync` | idem, com `AVERAGEX`; devolve `double` |
| `AnyAsync` | verifica `COUNTROWS(...) > 0` no servidor |
| `AllAsync` | conta as linhas que não atendem ao predicado |
| `Distinct` | `DISTINCT(...)` sobre a fonte — o uso natural é depois de um `Select` |
| `DistinctValuesAsync` | `DISTINCT(SELECTCOLUMNS(..., "Value", coluna))`, devolvendo `List<TValue>` **sem contrato intermediário** |
| `ValuesAsync` | o mesmo sem deduplicar — a projeção escalar |
| `Join` | `SELECTCOLUMNS(GENERATE(externo, FILTER(interno, chaves)), ...)` — os **dois** lados podem vir filtrados; chave composta por `new { a, b }` |

### Medidas do modelo

Em Power BI a lógica de negócio mora nas **medidas**. `[Total Vendas]` e `[Margem %]` já foram
escritas e revisadas pelo time de BI, e são a fonte oficial do número que aparece no
relatório. Recalcular isso em C# duplica a regra e arrisca divergir — o pior tipo de bug num
contexto de indicadores, porque o número parece plausível.

```csharp
decimal total = await contexto.Vendas
    .Where(v => v.Ano == 2024)
    .MeasureAsync<decimal>("Total Vendas");
```

```dax
EVALUATE ROW("[Value]", CALCULATE([Total Vendas], FILTER(Venda, Venda[Ano] = 2024)))
```

O nome vai com ou sem colchetes: `"Total Vendas"` e `"[Total Vendas]"` resolvem igual. No Power
BI a medida aparece entre colchetes, e exigir que fossem removidos produziria `[[Total Vendas]]`
— inválido, e só descoberto no servidor.

**Medida não é coluna, e confundir as duas dá número errado sem erro nenhum.**

| | coluna | medida |
|---|---|---|
| existe | por linha | já agregada |
| precisa de | iterador (`SUMX`) | contexto de filtro (`CALCULATE`) |
| escrita | `Venda[Total]` | `[Total Vendas]` |

Uma coluna só tem valor dentro de um contexto de linha — é por isso que `SumAsync` emite `SUMX`
e não `SUM` (ver [Os agregados escalares e o contexto de filtro](#os-agregados-escalares-e-o-contexto-de-filtro)).
Uma medida já é a agregação: envolvê-la num `SUMX` a somaria **uma vez por linha do iterador**,
gerando DAX que o servidor aceita e responde errado. Por isso os filtros entram como `CALCULATE`,
isto é, como recorte sobre o qual avaliá-la — e nunca como fonte iterada.

Sem filtro nenhum não sai `CALCULATE`: `CALCULATE([Medida])` sem argumento de filtro não faz nada
além de acrescentar uma função ao texto.

`BLANK` continua não sendo zero — uma medida sobre recorte vazio devolve `null`, pela mesma
decisão de `MinAsync` e `AverageOrDefaultAsync`.

**Depois de `Take`, `Skip`, `Select`, `GroupBy` ou `Join` a medida é recusada.** Uma janela não
vira argumento de contexto de filtro sem mudar o significado, e passá-la assim a descartaria em
silêncio — a medida seria avaliada sobre o modelo inteiro. É a mesma recusa que o agrupamento faz.

**Medida como coluna de um agrupamento** entra pelo grupo, não por atributo:

```csharp
.GroupBy(v => v.Categoria)
.Select(g => new PorCategoria
{
    Total = g.Measure<decimal>("Total Vendas"),
    Soma  = g.Sum(v => v.Valor)          // convive com agregado de coluna
})
```
```dax
EVALUATE SUMMARIZECOLUMNS(Venda[Categoria], "Total", [Total Vendas], "Soma", SUM(Venda[Valor]))
```

Quando uma coluna precisa de um escopo próprio, use `MeasureWhere`. O predicado vira um argumento
de filtro do `CALCULATE` apenas daquela coluna; outras medidas e agregações da mesma projeção
continuam no contexto original:

```csharp
var tipos = new[] { "00", "01" };

.GroupBy(v => v.Categoria)
.Select(g => new PorCategoria
{
    Total = g.Measure<decimal>("Total Vendas"),
    ApenasTipos = g.MeasureWhere<decimal>(
        "Total Vendas", v => tipos.Contains(v.Tipo.Codigo))
})
```

```dax
EVALUATE SUMMARIZECOLUMNS(
    Venda[Categoria],
    "Total", [Total Vendas],
    "ApenasTipos", CALCULATE(
        [Total Vendas],
        FILTER(DIM_TIPO, DIM_TIPO[CODIGO] IN { "00", "01" })
    )
)
```

`SumWhere` aplica a mesma ideia a uma agregação de coluna:
`g.SumWhere(v => v.Valor, v => v.Ano == 2024)` vira
`CALCULATE(SUM(Venda[Valor]), FILTER(Venda, Venda[Ano] = 2024))`. O seletor de `Sum` também aceita
condicional, que vira `IF` dentro do `SUMX`: `g.Sum(v => v.Ativo ? v.Valor : 0)`.

Aqui também **sem iterador em volta**: a medida já é a agregação, e um `SUMX` em torno dela a
somaria uma vez por linha do grupo. O nome resolve pelo mesmo ponto que `MeasureAsync` usa, então
as duas formas nunca divergem na regra de qualificação.

O nome precisa ser conhecido na composição — literal ou variável capturada. Um nome que dependa do
grupo teria de ser resolvido por linha, e o DAX não tem essa forma.

> Modificadores de filtro e escopos adicionais por coluna são emitidos dentro de `CALCULATE`.

### Contexto de filtro: `CALCULATE` e os modificadores

`Where` **aplica** filtro. Para os cálculos que dependem de **remover** filtro — participação no
total, comparação com o total da região — o DAX usa modificadores dentro de `CALCULATE`:

```csharp
tabela
    .GroupBy(v => v.Categoria)
    .Select(g => new PorCategoria
    {
        Categoria = g.Key,
        Total = g.Measure<decimal>("Total Vendas"),
        Participacao = g.Measure<decimal>("Total Vendas")
                       / g.MeasureIgnoring<decimal>("Total Vendas", v => v.Categoria)
    });
```

```dax
EVALUATE
SUMMARIZECOLUMNS(
    Venda[Categoria],
    "Total", [Total Vendas],
    "Participacao", [Total Vendas] / CALCULATE([Total Vendas], REMOVEFILTERS(Venda[Categoria]))
)
```

Cada linha é avaliada com o filtro da própria categoria; removê-lo dá o total sobre todas elas, **na
mesma consulta**. Somar no cliente não é equivalente — e nem é possível com paginação, onde o
cliente só vê a página.

| método | DAX | o que faz com o filtro |
|---|---|---|
| `MeasureIgnoring(m, v => v.Uf)` | `REMOVEFILTERS(Venda[Uf])` | **remove** o daquela coluna |
| `MeasureIgnoring(m)` | `REMOVEFILTERS(Venda)` | **remove** o da tabela inteira |
| `MeasureKeepingOnly(m, v => v.Regiao)` | `ALLEXCEPT(Venda, Venda[Regiao])` | **remove** todos menos o listado |

Os três existem também na medida escalar, como `MeasureIgnoringAsync` e `MeasureKeepingOnlyAsync`.
Ali o `Where` composto entra como contexto de filtro e o modificador **desfaz** a parte dele que
fala da coluna informada — os dois no mesmo `CALCULATE`.

#### Por que modificador de filtro não é predicado

**É a razão de a API ter esta forma, e não outra.** Dentro de um `CALCULATE` as duas coisas ocupam a
**mesma posição sintática** e fazem o **oposto**:

```dax
CALCULATE([Total Vendas], Venda[Uf] = "SP")          -- APLICA um filtro
CALCULATE([Total Vendas], REMOVEFILTERS(Venda[Uf]))  -- REMOVE um filtro
```

Uma API que tratasse modificador como predicado comum geraria DAX que diz o oposto do que quem
escreveu quis — e o número sairia plausível, que é o pior modo de falha num contexto de indicadores.

Por isso o parâmetro é um **seletor de coluna**, e não um predicado. E porque
`v => v.Uf == "SP"` também é um seletor válido para o compilador, passá-lo é **recusado** nomeando a
diferença:

```
A filter-context modifier takes a column, not a predicate. REMOVEFILTERS and ALLEXCEPT remove
filters from a column; a predicate applies one, and inside CALCULATE the two sit in the same
position and do opposite things. Pass a column selector — x => x.Uf — and, to apply a filter,
compose Where before reading the measure.
```

Dois detalhes de emissão, pelo mesmo motivo — o DAX gerado não deve poder surpreender:

- **`MeasureIgnoring` sem coluna emite `REMOVEFILTERS(Venda)`**, e não `REMOVEFILTERS()`. O segundo
  remove o filtro de **tudo**, inclusive de tabelas que a consulta nem menciona: poderoso demais
  para sair de um argumento omitido.
- **`MeasureKeepingOnly` sem coluna é recusado.** Seria igual a ignorar a tabela inteira, e o
  `ALLEXCEPT` de um argumento só faz quem lê o DAX perguntar o que foi excetuado.

E `CALCULATE` **não** é emitido quando não há nada que altere o contexto: `CALCULATE([Medida])` é
igual a `[Medida]`, e a função a mais só acrescentaria ruído a toda leitura de medida sem filtro.

### Linha de subtotal no agrupamento

Um grid com rodapé de total precisa das linhas de detalhe **e** da linha de total. `WithSubtotal()`
traz as duas na mesma consulta, e a coluna `[is_total]` diz qual é qual:

```csharp
var linhas = await contexto.Vendas
    .Where(v => v.Ativo)
    .GroupBy(v => new { v.CnpjRaiz, v.RazaoSocial })
    .WithSubtotal()
    .Select(g => new GanhoPorEmpresa { Realizado = g.Sum(v => v.Valor) })
    .ToListAsync();

linhas.Where(l => !l.IsTotal)   // o detalhe
linhas.Single(l => l.IsTotal)   // o rodapé
```

```dax
EVALUATE SUMMARIZECOLUMNS(
    ROLLUPADDISSUBTOTAL(ROLLUPGROUP(Venda[CnpjRaiz], Venda[RazaoSocial]), "is_total"),
    FILTER(Venda, Venda[Ativo]),
    "Realizado", SUM(Venda[Valor]))
```

**Somar as linhas no cliente não é equivalente.** Só coincide para medida aditiva. Para
`DISTINCTCOUNT`, média, razão (`% de economia`, ticket médio) ou medida semiaditiva, o total do
motor é calculado no contexto de filtro do total — e a soma das linhas dá número errado, plausível
o bastante para passar em revisão. Além disso, total no cliente exige trazer **todas** as linhas,
o que a paginação impede.

`ROLLUPGROUP`, e não `ROLLUP`: as chaves de um mesmo nível são tratadas como **um** nível e
devolvem uma linha de total só. CNPJ e razão social são o mesmo grão escrito em duas colunas.

**O contrato precisa declarar `[DaxColumn("[is_total]")]`.** Sem ela a linha de total chegaria como
linha de detalhe com as chaves em branco — o grid mostraria uma categoria vazia com o valor do
total, e a soma da coluna daria o dobro. A materialização não tem como notar, porque descartar
coluna não mapeada em silêncio é o comportamento certo para todo o resto. Então a composição
recusa, nomeando o que falta.

**`Take` e `Skip` depois de `WithSubtotal` são recusados.** A linha de total não é uma linha de
detalhe, e o `TOPN` não sabe disso: ela entraria na contagem da página e apareceria no meio dela,
ou sumiria conforme a página pedida.

#### Hierarquia de dois ou mais níveis

Uma matriz período × exportador precisa de **três** totais na mesma consulta: a célula, o total do
período e o total geral. Cada `WithSubtotal(nome, chaves)` acrescenta um nível — um subconjunto da
chave do `GroupBy`, com a própria coluna de flag:

```csharp
var linhas = await contexto.Faturamento
    .GroupBy(f => new { f.PeriodoId, f.PeriodoNome, f.CnpjRaiz, f.RazaoSocial })
    .WithSubtotal("is_period_total", f => new { f.PeriodoId, f.PeriodoNome })
    .WithSubtotal("is_exporter_total", f => new { f.CnpjRaiz, f.RazaoSocial })
    .Select(g => new LinhaMatriz { Realizado = g.Sum(f => f.Valor) })
    .ToListAsync();
```

```dax
EVALUATE SUMMARIZECOLUMNS(
    ROLLUPADDISSUBTOTAL(
        ROLLUPGROUP(Faturamento[PeriodoId], Faturamento[PeriodoNome]), "is_period_total",
        ROLLUPGROUP(Faturamento[CnpjRaiz], Faturamento[RazaoSocial]), "is_exporter_total"),
    "Realizado", SUM(Faturamento[Valor]))
```

**A ordem das chamadas é a hierarquia**, não uma lista solta: a primeira `WithSubtotal` é o nível
**externo**, a última o **interno** — inverter a ordem inverte o significado de cada combinação de
flags. E a hierarquia não devolve todas as combinações: com dois níveis, as linhas voltam em três
formas só.

| `is_period_total` | `is_exporter_total` | significado |
| --- | --- | --- |
| `FALSE` | `FALSE` | célula período × exportador |
| `FALSE` | `TRUE` | total do período (exportador fechado) |
| `TRUE` | `TRUE` | total geral |
| `TRUE` | `FALSE` | **não existe** |

Pedir o nível que falta — exportador com período agregado — devolve lista **vazia**, sem erro. Se
esse nível é necessário, ele precisa de uma consulta própria.

Cada nível continua exigindo a própria coluna de flag no contrato (`is_period_total` e
`is_exporter_total`, nesse exemplo), e um nível cujas colunas não estão na chave do `GroupBy` é
recusado na composição — antes de qualquer ida ao servidor.

`WithSubtotal()` sem argumentos (nível único, cobrindo a chave inteira) e a forma com nível não se
combinam: chamar a primeira depois de já ter um nível é recusado.

### Paginação: a página e o total numa consulta

Um grid paginado precisa das linhas da página **e** da contagem total. Feito com
`ToListAsync` mais `CountAsync`, isso são duas idas ao endpoint — duas conexões, duas
filas de capacity, duas varreduras — e abre janela de inconsistência: um refresh entre
elas devolve página de um estado e total de outro.

```csharp
DaxPage<Produto> pagina = await contexto.Produtos
    .Where(p => p.Ativo)
    .OrderBy(p => p.Id)
    .Skip(20)
    .Take(10)
    .ToPagedListAsync();

pagina.Items   // as 10 linhas
pagina.Total   // o total do conjunto filtrado, antes da janela
```

```dax
DEFINE
    VAR __pl_source_0 = FILTER(Produto, Produto[Ativo])
    VAR __pl_total = COUNTROWS(__pl_source_0)
EVALUATE
ADDCOLUMNS(
    TOPN(10, EXCEPT(__pl_source_0, TOPN(20, __pl_source_0, Produto[Id], ASC)), Produto[Id], ASC),
    "[__pl_total]", __pl_total)
ORDER BY Produto[Id] ASC
```

O total sai de uma `VAR`, e não escrito dentro do `ADDCOLUMNS`: ali ele seria a expressão
de uma coluna calculada, **avaliada por linha da página**. E ele conta o conjunto anterior
à janela — contar depois de `Skip`/`Take` daria o tamanho da página, que o chamador já sabe.

O total é `long`, e não `int`, pelo mesmo motivo de `LongCountAsync` existir: `COUNTROWS`
sobre tabela fato passa de dois bilhões.

**Exige um executor que também implemente `IDaxRawQueryExecutor`.** A página e o total vêm
na mesma linha, e materializar direto no contrato descartaria a coluna do total — ela não
está mapeada, e nem deveria estar, porque é detalhe da consulta e não do domínio.
`XmlaQueryExecutor` e `PooledXmlaQueryExecutor` implementam; um executor próprio que não
implemente continua servindo todo o resto, e `ToPagedListAsync` recusa nomeando o motivo.

**Página além do fim paga uma segunda ida.** `ADDCOLUMNS` carimba o total em cada linha —
sem linha, não há carimbo. Nesse caso o total é buscado com a mesma contagem que
`CountAsync` emite, então `Skip(1000)` sobre 87 linhas devolve `Items` vazio e `Total` 87.
O caso normal continua sendo uma consulta só.

### Os agregados escalares e o contexto de filtro

Os agregados usam a forma **iteradora** do DAX (`SUMX`) e não a escalar (`SUM`), e a
diferença não é de estilo:

```dax
-- o que PowerLinq gera para Where(p => p.Ativo).SumAsync(p => p.Preco)
EVALUATE ROW("[Value]", SUMX(FILTER(Produto, Produto[Ativo]), Produto[Preco]))

-- a forma escalar: também é DAX válido, e responde outra pergunta
EVALUATE ROW("[Value]", SUM(Produto[Preco]))
```

`SUM` soma no **contexto de filtro** corrente, que numa consulta sem `CALCULATE` é o
modelo inteiro — o `FILTER` seria ignorado e o resultado viria da tabela toda, **sem
erro**. `SUMX` abre **contexto de linha** sobre a fonte já filtrada, que é o que
corresponde à semântica do LINQ.

Sobre nenhuma linha, todo agregado iterador do DAX devolve `BLANK` — inclusive `SUMX`,
que **não** devolve zero. O comportamento espelha o LINQ:

| operador | sobre nenhuma linha |
|---|---|
| `SumAsync` | `0`, ou `null` quando o seletor é anulável |
| `MinAsync` / `MaxAsync` | `null` se o tipo aceitar nulo; lança se não aceitar |
| `AverageAsync` | lança `InvalidOperationException` |
| `AverageOrDefaultAsync` | `null` |

`AverageAsync` devolve `double` mesmo sobre coluna inteira: tipar o retorno como a
coluna faria `AverageAsync(x => x.Quantidade)` truncar 2,5 para 2.
| `Join` | equijoin com `GENERATE`, `FILTER` e `SELECTCOLUMNS` |
| `ToDaxString` | o DAX como texto, sem executar |

Dentro de um seletor de `Aggregate` ou `GroupBy`, `IDaxAggregate<T>` oferece:

| C# | DAX gerado |
|---|---|
| `a.Count()` | `COUNTROWS(...)` da tabela da consulta |
| `a.Count<TOutra>()` | `COUNTROWS('tabela de TOutra')` — porteira de existência, ver abaixo |
| `a.CountDistinct(x => x.Campo)` | `DISTINCTCOUNT(...)` — só coluna, ver abaixo |
| `a.Sum(x => x.Campo)` | `SUM(...)` para coluna pura, `SUMX(...)` para expressão |
| `a.SumWhere(x => x.Campo, x => predicado)` | `CALCULATE(SUM/SUMX(...), FILTER(...))` só nesta coluna |
| `a.Average(x => x.Campo)` | `AVERAGE(...)` / `AVERAGEX(...)` |
| `a.Min(x => x.Campo)` | `MIN(...)` / `MINX(...)` |
| `a.Max(x => x.Campo)` | `MAX(...)` / `MAXX(...)` |

`Sum`, `Average`, `Min` e `Max` cobrem `int`, `long`, `double` e `decimal`, com e
sem `?`, **preservando o tipo**: `Min` sobre coluna `decimal` devolve `decimal`,
sem passar por `double` no caminho. `Min` e `Max` também aceitam `DateTime`.

Um seletor de agregação pode usar o condicional `cond ? valor : outroValor`; ele vira `IF(cond,
valor, outroValor)` no DAX. Isso permite, por exemplo, somar apenas linhas ativas com
`g.Sum(x => x.Ativo ? x.Valor : 0)`.

`CountDistinct` exige uma coluna, porque o DAX não tem `DISTINCTCOUNTX` para
expressão calculada — uma expressão ali é recusada com mensagem explicando isso.

O nome da coluna de extensão vem do nome da propriedade; `[DaxColumn("[alias]")]`
é opcional e serve para renomear:

```csharp
public sealed class Totais
{
    public decimal Total { get; set; }   // vira "Total"
    public long Qtd { get; set; }        // vira "Qtd"
}

await context.Vendas
    .Aggregate(g => new Totais { Total = g.Sum(x => x.Valor), Qtd = g.Count() })
    .FirstOrDefaultAsync();
```

Dentro de um predicado:

| C# | DAX |
|---|---|
| `==` `!=` `>` `>=` `<` `<=` | `=` `<>` `>` `>=` `<` `<=` |
| `&&` `\|\|` `!` | `&&` `\|\|` `NOT(...)` |
| `+` `-` `*` `/` (numérico) | `+` `-` `*` `/` |
| `+` (texto) | `&` — o operador de concatenação, não a soma |
| `null` | `BLANK()` |
| `bool` | `TRUE` / `FALSE` |
| `DateTime` | `DATE(ano,mês,dia)` |
| variável capturada | avaliada na tradução, vira literal |
| qualquer expressão que não depende do parâmetro | idem — `new DateTime(...)`, `DateTime.Today.AddDays(-30)`, chamada estática fechada |
| `colecao.Contains(coluna)` | `coluna IN { v1, v2, ... }` |

Pertencimento a conjunto — o filtro multi-seleção — aceita `List<T>`, `T[]`,
`HashSet<T>` e qualquer `IEnumerable<T>`, com os valores avaliados na tradução:

```csharp
var categorias = new[] { "Eletrônicos", "Móveis" };
p => categorias.Contains(p.Categoria)
// Produto[Categoria] IN { "Eletrônicos", "Móveis" }

p => !categorias.Contains(p.Categoria)
// NOT(Produto[Categoria] IN { "Eletrônicos", "Móveis" })
```

Coleção **vazia** vira `FALSE`, porque `IN { }` é erro de sintaxe em DAX e conjunto
vazio não casa com nada — e a forma negada vira `NOT(FALSE)`, sem tratamento
especial. Coleção **nula** lança, em vez de virar um filtro que não casa nada por
acidente.

Métodos de texto suportados:

| C# | DAX gerado |
|---|---|
| `texto.Contains(valor)` | `SEARCH(valor, texto, 1, 0) > 0` |
| `texto.StartsWith(valor)` | `LEFT(texto, LEN(valor)) = valor` |
| `texto.EndsWith(valor)` | `RIGHT(texto, LEN(valor)) = valor` |
| `texto.ToUpper()` / `ToLower()` | `UPPER(texto)` / `LOWER(texto)` |
| `texto.Trim()` | `TRIM(texto)` |
| `texto.Length` | `LEN(texto)` |
| `texto.Substring(i, n)` | `MID(texto, i+1, n)` — `MID` é base 1 |
| `texto.Substring(i)` | `MID(texto, i+1, LEN(texto))` |
| `texto.Replace(de, para)` | `SUBSTITUTE(texto, de, para)` |
| `texto.IndexOf(valor)` | `SEARCH(valor, texto, 1, 0) - 1` — base 0, e `-1` quando não encontra |
| `string.IsNullOrEmpty(texto)` | `ISBLANK(texto) || texto = ""` |
| `string.IsNullOrWhiteSpace(texto)` | `ISBLANK(texto) || TRIM(texto) = ""` |

Os ajustes de índice não são cosméticos: `MID` é base 1 e `Substring` base 0, e
`SEARCH` devolve `0` quando não encontra enquanto `IndexOf` devolve `-1`. Sem eles
o resultado sairia deslocado sem falhar.

Texto para número, sobre uma coluna:

| C# | DAX gerado |
|---|---|
| `int.Parse(texto)` | `VALUE(texto)` |
| `long.Parse` / `decimal.Parse` / `double.Parse` | idem — em DAX não há função por tipo |
| `Convert.ToInt32(texto)` e as demais sobre **texto** | idem |
| `int.Parse(texto, cultura)` | **recusado** — ver abaixo |
| `Convert.ToInt32(numero)` | **recusado** — ele arredonda para o par mais próximo |

> **A cultura diverge, e não há como resolver isso na tradução.** `VALUE` interpreta o
> texto pela **locale do modelo**; `int.Parse` sem `IFormatProvider` usa a cultura corrente
> do processo. `VALUE("1.234")` é mil duzentos e trinta e quatro num modelo pt-BR e um e
> pouco num en-US. O tradutor não conhece a locale do modelo, então prometer equivalência
> com `int.Parse` seria falso.
>
> É por isso que a sobrecarga **com** `IFormatProvider` é recusada em vez de traduzida:
> honrá-la exigiria impor a cultura ao servidor, e traduzi-la assim mesmo descartaria em
> silêncio o argumento que quem escreveu passou justamente para não depender da cultura.
>
> A materialização resolve a versão espelhada desse problema usando cultura invariante na
> **leitura** (ver `DaxValueConverter`). Aqui a conversão acontece no **servidor**, onde não
> temos essa escolha.

`int.Parse(variavel)` de uma variável capturada continua virando literal — não depende do
parâmetro do lambda, então é avaliado na tradução e o servidor não vê conversão nenhuma.

`int.TryParse` é **recusado com mensagem própria**, e não com a genérica de método sem tradução:
o motivo não é falta de implementação, são duas incompatibilidades de semântica que nenhuma
implementação removeria. O parâmetro `out` não tem para onde ir — uma expressão DAX devolve **um**
valor, e não há segundo canal de saída. E `TryParse` promete **não lançar**, devolvendo `false` no
texto que não converte, enquanto `VALUE` levanta erro ali; traduzir assim mesmo trocaria esse
`false` por uma consulta que falha. A saída é `int.Parse`, filtrando antes dele as linhas cujo
texto não converte.

> Esta seção afirmava que `TryParse` **não chegava** ao tradutor, porque o compilador não permite
> `out` numa árvore de expressão. A afirmação era meia verdade, e a metade que faltava importava:
> o compilador recusa a declaração inline (`out int v`, CS8198) e o descarte (`out _`, CS8207),
> mas `out` sobre variável **já declarada** ou sobre **campo** compila e chega. Era uma limitação
> documentada sem teste — e a única do README que não tinha um em `ReadmeLimitationsTests`.

`DateTime.TryParse` continua caindo na recusa **genérica**, e isso é deliberado: a mensagem
própria manda usar `Parse`, e ali `Parse` também não tem tradução. Mandar trocar por algo que
também não funciona é pior que não dizer nada.

Partes de data e hora sobre uma coluna:

| C# | DAX gerado |
|---|---|
| `data.Year` / `Month` / `Day` | `YEAR(...)` / `MONTH(...)` / `DAY(...)` |
| `data.Hour` / `Minute` / `Second` | `HOUR(...)` / `MINUTE(...)` / `SECOND(...)` |
| `data.Date` | `DATE(YEAR(...), MONTH(...), DAY(...))` |
| `data.DayOfWeek` | `WEEKDAY(..., 1) - 1` — alinhado ao `DayOfWeek` do .NET, que começa em 0 |
| `coluna.Value` (anulável) | a própria coluna |

```csharp
p => p.Data.Year == 2024 && p.Data.Month >= 6
// YEAR(Produto[Data]) = 2024 && MONTH(Produto[Data]) >= 6

p => p.DataOpcional.Value.Year == 2024
// YEAR(Produto[DataOpcional]) = 2024
```

### Porteira de existência: `COUNTROWS` de outra tabela

Para montar lista de opções de filtro — "só os clientes que têm dado no período" —, o
idioma do DAX é contar linhas do **fato** como coluna de extensão:

```csharp
await context.Rls
    .Where(r => r.GrupoEmpresarialId == grupo)
    .GroupBy(r => r.Cnpj)
    .Select(g => new Opcao
    {
        Cnpj = g.Key,
        RealizadoRows = g.Count<FatoRealizado>()   // COUNTROWS(FAT_REALIZADO)
    })
    .ToListAsync();
```

**Isto não é uma métrica, é um filtro.** O `SUMMARIZECOLUMNS` descarta o grupo quando
**todas** as colunas de extensão vêm `BLANK`, então `COUNTROWS` do fato é o que poda a
dimensão para "só o que tem dado" — o comportamento de um slicer de Power BI.
Apontá-lo para a tabela errada não devolve número errado: **devolve a dimensão
inteira**, porque a poda deixa de acontecer. Medido num modelo real, agrupando uma
dimensão e filtrando por um grupo:

| coluna de extensão | linhas devolvidas |
|---|---:|
| `COUNTROWS(<fato>)` | **15** |
| `COUNTROWS(<tabela da consulta>)` | 254 |
| dimensão inteira, sem filtro (controle) | 254 |

Duas consequências práticas:

- **A propriedade deve ser `long?`.** `Count<TOutra>()` devolve `long?` de propósito:
  `COUNTROWS` de um grupo sem linhas é `BLANK`, e é justamente isso que faz a poda
  funcionar. Materializar para `long` transformaria `BLANK` em `0` e esconderia o
  efeito — a coluna deixaria de indicar "não existe dado" e passaria a afirmar
  "existem zero linhas", que é diferente.
- **Duas porteiras dão a união.** `Count<FatoRealizado>()` e `Count<FatoPrevisto>()` na
  mesma projeção mantêm o grupo se **qualquer** um dos dois tiver linha, porque o
  descarte exige que todas as extensões venham `BLANK`. Um `Where` sobre os dois seria
  interseção.

### Navegação entre entidades

O relacionamento é a informação mais importante de um modelo estrela, e como referência qualificada
em texto ele só existe como convenção dentro de uma string. `[DaxNavigation]` torna a navegação
escrevível:

```csharp
[DaxTable("FAT_ISENCAO")]
sealed class Fato
{
    [DaxColumn("FAT_ISENCAO[VALOR]")] public decimal Valor { get; set; }

    [DaxColumn("FAT_ISENCAO[EMPRESA_ID]")]
    [DaxNavigation("FAT_ISENCAO[EMPRESA_ID]")]
    public Empresa Empresa { get; set; } = new();
}
```

**A tradução depende da posição**, e é o ponto que decide tudo aqui:

| onde | DAX |
|---|---|
| predicado **separável** por tabela | `CALCULATETABLE(Fato, FILTER(DIM_EMPRESA, ...))` |
| predicado que compara **duas** tabelas | `FILTER(Fato, Fato[VALOR] > RELATED(DIM_EMPRESA[LIMITE]))` |
| chave de `GroupBy` | `DIM_EMPRESA[CNPJ_RAIZ]` — **sem** `RELATED` |
| projeção (`Select`) | `RELATED(DIM_EMPRESA[CNPJ_RAIZ])` |

As três primeiras numa consulta só:

```csharp
tabela
    .Where(r => r.Empresa.Grupo.Nome == "ACME")   // separável → tabela de filtro
    .GroupBy(r => r.Empresa.CnpjRaiz)             // chave → referência qualificada
    .Select(g => new PorEmpresa { Cnpj = g.Key, Total = g.Sum(r => r.Valor) });
```

```dax
EVALUATE
SUMMARIZECOLUMNS(
    DIM_EMPRESA[CNPJ_RAIZ],
    FILTER(DIM_GRUPO, DIM_GRUPO[NOME] = "ACME"),
    "Total", SUM(FAT_ISENCAO[VALOR])
)
```

Note que o filtro de dois saltos se aplica à tabela **do fim da cadeia**, e não à do meio: o
relacionamento propaga o filtro dali até o fato, que é o que um modelo estrela faz.

#### Navegação **não** é o caminho para filtrar por dimensão

**É a parte que mais importa desta seção.** Para filtrar, o caminho continua sendo o argumento de
tabela de filtro, e a biblioteca escolhe esse caminho sozinha quando o predicado é separável. Duas
razões, e nenhuma é de estilo:

1. **Direção.** `RELATED` atravessa **muitos→um**, e só. A dimensão está do lado *um* em relação ao
   fato, então partir do fato dá certo; uma tabela que esteja do lado *muitos* em relação à dimensão
   é inalcançável a partir do fato — e é justamente onde costuma morar o predicado de isolamento por
   tenant, que não pode falhar.
2. **Custo.** `RELATED` dentro de `FILTER` é iteração linha a linha no *formula engine*; o booleano
   de `CALCULATETABLE` desce como filtro para o *storage engine*. Mesmo resultado, custos de ordem
   diferente num fato grande.

É por isso que a tradução **prefere** o contexto de filtro e usa `RELATED` só onde ele é a única
forma: comparar colunas de tabelas diferentes. Emitir `RELATED` num predicado separável seria uma
armadilha de desempenho silenciosa.

#### Direção e ambiguidade: conferidas contra o esquema

Na tradução **não há como saber a cardinalidade** — a composição de uma consulta não toca a rede, e
essa propriedade não se abre mão. Quem confere é o `DaxContractValidator`, sobre o esquema versionado:

```
UntraversableDirection at Fato.Empresa: Navigating from 'FAT_ISENCAO' to 'DIM_EMPRESA' would need
one-to-many, and RELATED only crosses many-to-one: FAT_ISENCAO[EMPRESA_ID] 1—* DIM_EMPRESA[ID].
```

Ele recusa caminho **ambíguo** nomeando os dois, distingue caminho **inativo** de inexistente — a
saída ali é `USERELATIONSHIP`, não criar relacionamento — e confere **cada salto** de uma cadeia,
nomeando o que quebrou: `RELATED` atravessa a cadeia inteira, mas só se todos os elos forem
muitos→um.

> **Navegação não é materializada.** `EVALUATE Fato` devolve as colunas do fato, não a entidade da
> dimensão — a propriedade de navegação é ignorada pelo mapeamento, e ler valor de dimensão é papel
> de uma projeção. O lado *um→muitos* (`RELATEDTABLE`) está fora de escopo: aquilo é agregação, não
> navegação escalar.

### Filtrar por coluna de outra tabela

Num modelo estrela, a maior parte dos filtros de tela é sobre dimensão, não sobre
o fato. Mapeando a coluna com referência qualificada, o filtro funciona:

```csharp
[DaxTable("FAT_EXPORTACAO")]
public sealed class Exportacao
{
    [DaxColumn("FAT_EXPORTACAO[VL_REALIZADO]")]  public decimal Realizado { get; set; }
    [DaxColumn("'DIM_EMPRESA'[CNPJ]")]           public string Cnpj { get; set; } = "";
}

tabela.Where(e => e.Cnpj == "12345678000199")
```

```dax
EVALUATE
CALCULATETABLE(
    FAT_EXPORTACAO,
    FILTER(
        DIM_EMPRESA,
        'DIM_EMPRESA'[CNPJ] = "12345678000199"
    )
)
```

A forma depende da tabela dona da coluna, e a diferença **não** é estilística.
`FILTER(tabela, predicado)` abre **row context** na tabela que itera, e nesse
contexto referência a coluna de outra tabela — mesmo relacionada — é erro de DAX:
*"não é possível determinar um único valor para a coluna X na tabela Y"*.
`CALCULATETABLE` aplica o predicado como **contexto de filtro**, que se propaga
pelos relacionamentos do modelo. Row context e filter context não são
intercambiáveis.

Predicado misto é separado por tabela, um filtro para cada:

```csharp
tabela.Where(e => e.Realizado > 0 && e.Cnpj == "123")
// CALCULATETABLE(
//     FILTER(FAT_EXPORTACAO, FAT_EXPORTACAO[VL_REALIZADO] > 0),
//     FILTER(DIM_EMPRESA, 'DIM_EMPRESA'[CNPJ] = "123"))
```

Na consulta agrupada os mesmos filtros entram como argumentos de tabela de filtro
do `SUMMARIZECOLUMNS`, que já é contexto de filtro e não precisa do
`CALCULATETABLE` em volta.

Uma **mesma condição** não pode falar de duas tabelas — `e.Cnpj == e.Grupo`
comparando colunas de tabelas diferentes, ou um `||` cruzando fato e dimensão.
Um filtro se aplica a uma tabela, e a interseção de filtros é uma conjunção: `&&`
separa, `||` não. O caso é recusado nomeando as tabelas envolvidas.

## Arquitetura

```
src/
  PowerLinq.DaxConverter/       LINQ -> DAX, sem dependências de projeto
    Attributes/                 DaxTableAttribute, DaxColumnAttribute
    Execution/                  IDaxQueryExecutor
    Mapping/                    EntityMapper — reflexão e materialização
    Context/                    DaxContext e opções  (ver Limitações)
    Syntax/                     a árvore DAX e sua escrita
    Linq/                       IQueryable e o provider (-> DaxPipeline)
    DaxTable<T>, DaxQuery<T>    composição imutável da consulta
    DaxExpressionVisitor        expressão C# -> árvore DAX
    DaxStageTranslator          operador + lambda -> DaxStage (as duas superfícies)
    DaxPipeline, DaxStage       a consulta como sequência ordenada de operadores
    DaxPipelineBuilder          pipeline -> árvore DAX

  PowerLinq.ConnectionPool/     XmlaQueryExecutor, ADOMD.NET (-> DaxConverter)

  PowerLinq.Abstractions/       vazio
  PowerLinq.Cache/              vazio
```

O ponto central é `DaxConverter/Syntax`: a consulta nunca existe como texto até
o fim.
`Where` compõe nós, não strings, e cada nó sabe escrever a si mesmo:

```csharp
public sealed record DaxFilter(IDaxTableExpression Source, IDaxExpression Predicate)
    : IDaxTableExpression
{
    public void Write(DaxWriter writer) =>
        writer.Append("FILTER(")
              .Indent().Break()
              .Write(Source)
              .Separator()
              .Write(Predicate)
              .Outdent().Break()
              .Append(')');
}
```

Isso compra três coisas que a concatenação de strings não dava:

- **Tipos separam escalar de tabela.** `DaxFilter` só aceita `IDaxTableExpression`
  como fonte, então construções inválidas param no compilador em vez do servidor.
- **Parênteses por precedência.** `(A || B) && C` mantém os parênteses;
  `A && B || C` os omite. Um nó que devolvesse string isolada teria de
  parentetizar sempre.
- **Cultura invariante.** Números são formatados com `InvariantCulture`. Em
  `pt-BR`, `100.5m` viraria `100,5` — e a vírgula separa argumentos em DAX.

A **leitura** do resultado segue a mesma regra, e por um motivo simétrico: o
formato dos valores que vêm do XMLA é do protocolo, não da preferência do
usuário. Sob `pt-BR`, uma conversão sem cultura leria a string `"1234.56"` como
**123456** — o ponto tratado como separador de milhar, um erro de 100x — e
`"03/08/2026"` como 3 de agosto em vez de 8 de março, sem falhar. Toda a
materialização usa `InvariantCulture`, então data ambígua é sempre lida com o
**mês primeiro**.

A árvore fica acessível antes da escrita, para reescrita ou chave de cache:

```csharp
DaxEvaluate arvore = produtos.Where(p => p.Ativo).ToSyntaxTree();
```

Vale para qualquer composição, inclusive depois de `Select`, `GroupBy`/`Aggregate` e
`Join`: os três devolvem `DaxQuery<TResult>`, e `ToSyntaxTree` tem uma implementação só.

## Performance

Medição de 2026-08-02. A suíte completa são 124 casos, executados em 26min19s.

> **Números anteriores ao cache de reflexão.** Só a seção
> [Custo de um predicado simples](#custo-de-um-predicado-simples) foi remedida
> depois de `ResolveColumnReference` passar a cachear a resolução de `[DaxColumn]`.
> As demais seções desta página — escalabilidade, composição e materialização —
> continuam refletindo o estado sem esse cache, então os tempos e as alocações que
> envolvem acesso a propriedade atributada estão **superestimados** ali. A forma
> das curvas e as conclusões relativas seguem válidas; os valores absolutos, não.

> O estado medido foi revertido em seguida, por remover API pública. O que ele
> tinha de diferente eram tipos de retorno mais estreitos e reescritas de
> `if`/`switch` preservando comportamento, então os números devem seguir
> representativos — mas não foram remedidos após a reversão.

### Metodologia

```bash
dotnet run --project tests/PowerLinq.Benchmark -c Release -- --filter '*'
```

| Item | Valor |
|---|---|
| Ferramenta | BenchmarkDotNet v0.15.8, `MemoryDiagnoser` ligado |
| Iterações | 3 warmups + 10 iterações por caso |
| Runtime | .NET 10.0.5, X64 RyuJIT x86-64-v3 |
| Máquina | AMD Ryzen 7 5800X, 8 núcleos físicos / 16 lógicos, Linux Mint 22.3 |

**O que entra na medição, e o que não entra.** Nos benchmarks do visitor as
árvores de expressão são construídas no `[GlobalSetup]`. Mede-se a tradução
árvore → DAX, não o custo que o compilador paga para materializar a
`Expression<Func<>>`. Já os benchmarks ponta a ponta (`EndToEndQueryBenchmarks`)
partem do lambda escrito pelo usuário e portanto **incluem** esse custo — é por
isso que os dois grupos operam em ordens de grandeza diferentes e não devem ser
comparados entre si.

Nenhum benchmark toca a rede: o executor é um *noop*. Os números medem tradução,
composição e materialização, nunca latência de XMLA.

Como os resultados são sensíveis à máquina, o valor de comparar está no **ratio
entre casos** e na **forma da curva de escala**, não nos nanossegundos absolutos.

### Escalabilidade

O resultado mais relevante: tudo cresce linearmente, sem joelho na curva.

| Componente | 1 termo | 32/64 termos | Fator medido | Linear seria |
|---|---:|---:|---:|---:|
| Visitor — cadeia `AND` | 917 ns | 58.349 ns (64) | 63,6x | 64x |
| Composição — `N x Where` | 862 ns | 28.271 ns (32) | 32,8x | 32x |
| Builder — `N` termos em `ORDER BY` | 105 ns | 678 ns (32) | 6,4x | sublinear¹ |

¹ No builder o custo fixo por consulta domina, então dobrar os termos não dobra
o tempo.

A alocação acompanha: o visitor vai de 696 B a 34.056 B entre 1 e 64 predicados.

**O formato da árvore é irrelevante.** Cadeia esquerda-profunda e árvore
balanceada ficam dentro de 2% em todos os sete tamanhos, com alocação idêntica
byte a byte. Não há penalidade de profundidade de recursão até 64 níveis.

### Custo de um predicado simples

Medição original, antes de haver cache de reflexão:

| Caso | Tempo | Alocado |
|---|---:|---:|
| `p => p.Categoria == "Eletrônicos"` (propriedade **com** `[DaxColumn]`) | 965 ns | 856 B |
| `p => p.Observacao == "nota"` (propriedade **sem** `[DaxColumn]`) | 187 ns | 504 B |

Os dois predicados têm forma idêntica — mesma comparação, mesmo tipo, mesma
estrutura de árvore. A única diferença era o atributo, e ela respondia por
**778 ns dos 965**: `GetCustomAttribute` materializa uma instância nova do
atributo a cada acesso de propriedade, e era ele que dominava o caminho quente —
não a travessia da árvore.

**Isso foi corrigido.** `ResolveColumnReference` passou a cachear, por
`PropertyInfo`, a referência declarada no atributo. Medição A/B na mesma máquina,
executada em sequência:

| Caso | Antes | Depois |
|---|---:|---:|
| propriedade **com** `[DaxColumn]` | 950 ns | **255 ns** |
| propriedade **sem** `[DaxColumn]` | 209 ns | 233 ns |
| razão entre os dois | 4,54x | **1,09x** |

A penalidade do atributo praticamente desapareceu. O caso sem atributo fica
dentro da margem de erro: o cache troca uma varredura de metadados por um lookup
de dicionário, e nenhum dos dois domina.

O sinal mais confiável é a **alocação**, que não depende de carga da máquina:

| Predicado | Propriedades | Antes | Depois | Economia |
|---|---:|---:|---:|---:|
| igualdade simples | 1 | 856 B | 592 B | 264 B |
| `AndAlso` | 2 | 1.264 B | 744 B | 520 B |
| `AND`/`OR` misto | 4 | 2.392 B | 1.352 B | 1.040 B |
| **sem `[DaxColumn]`** | **0** | **504 B** | **504 B** | **0 B** |

São ~260 B por acesso a propriedade atributada, exatamente lineares no número de
termos, e exatamente zero quando não há atributo — o tamanho de uma instância de
`DaxColumnAttribute`. É a confirmação direta do que o cache elimina.

Os oito tipos de literal (string, int, decimal, bool, `DateTime`, `null`,
escaping) medem entre 919 ns e 1.029 ns — indistinguíveis dentro do erro. Nenhum
ramo de formatação é patologicamente caro.

### Composição e execução

| Caso | Tempo | vs. baseline |
|---|---:|---:|
| `Take` (só `record with`) | 16 ns | 53x mais rápido |
| `Where` + `ToDaxString`, lambda pré-construída | 1.094 ns | 1,73x mais rápido |
| `Where` + `ToDaxString`, lambda inline | 1.891 ns | baseline |
| `Where` composto + `ToDaxString`, inline | 4.508 ns | 2,38x mais lento |
| Montar a árvore sem renderizar | 25 ns | — |
| `EVALUATE Tabela` (árvore + escrita) | 43 ns | — |

Três leituras:

- **`Take` é essencialmente grátis.** É um `record with` sobre a definição, sem
  trabalho de árvore de expressão; custa 16 ns em qualquer tamanho de cadeia.
- **Reaproveitar a `Expression` compensa.** A mesma consulta com lambda
  pré-construída em vez de inline roda 1,73x mais rápido, porque o custo do
  compilador materializar o lambda sai do caminho.
- **Renderizar custa mais que montar.** 25 ns para a AST contra 43 ns com a
  escrita — a árvore intermediária não é o gargalo que se poderia supor.

### Materialização

O mapeamento de colunas é resolvido **uma vez por tipo** e a atribuição usa setter
compilado, não reflexão. `GetColumnMappings` custava 8.081 ns e 3.680 B por
chamada; hoje custa **4,7 ns e nada**:

| | antes | depois |
|---|---:|---:|
| `GetColumnMappings` | 8.081 ns · 3.680 B | **4,7 ns · 0 B** |
| `MapRow` (1 linha, 9 colunas) | 482 ns · 176 B | **172 ns · 104 B** |

E a materialização por volume, medindo a variante que resolve o mapeamento a cada
execução contra a que o recebe pronto:

| Linhas | Antes (sem resolver) | Depois (sem resolver) | Depois (resolvendo) | Alocação depois |
|---:|---:|---:|---:|---:|
| 1 | 613 ns | **184 ns** | 181 ns | 168 B ≡ 168 B |
| 10 | 8.481 ns | **1.820 ns** | 2.415 ns | 1.176 B ≡ 1.176 B |
| 100 | 84,1 µs | **22,4 µs** | 24,3 µs | 11.256 B ≡ 11.256 B |
| 1.000 | — | **245 µs** | 208 µs | 112.056 B ≡ 112.056 B |
| 10.000 | 8,27 ms | **2,08 ms** | 2,14 ms | 1.120.056 B ≡ 1.120.056 B |

Duas leituras:

**A penalidade de resolver o mapeamento desapareceu.** Era 14,3x na consulta de uma
linha (613 ns contra 8.755 ns). Agora as duas variantes medem igual — e o que prova
isso não é o tempo, é a **alocação idêntica** nos cinco tamanhos: resolver o
mapeamento passou a alocar exatamente zero. Onde os tempos divergem (1,33x na de 10
linhas, 1,19x *a favor* da variante que faz trabalho a mais na de 1.000) é ruído,
como o `RatioSD` de 0,13 e 0,12 indica.

**A materialização em si ficou ~4x mais rápida e aloca 1,5x menos.** O ganho vem de
sair do caminho quente o que não depende da linha: `Nullable.GetUnderlyingType` era
resolvido **por célula** — 90.000 vezes num resultado de 10.000 linhas com 9 colunas
— e `PropertyInfo.SetValue` aloca a cada chamada. Os 576 KB que somem em 10.000
linhas são ~6 B por célula que a reflexão cobrava.

> Comparações de **tempo** entre antes e depois são de execuções separadas, então
> valem como ordem de grandeza, não como medida — é por isso que a coluna de
> alocação está aqui: ela é exata e não depende da máquina estar quieta.

#### O caminho do construtor

Materializar um contrato imutável custa mais, e o quanto foi medido na mesma execução
que o caminho de propriedades — as duas colunas abaixo são comparáveis entre si:

| Linhas | Propriedade | Construtor | | Propriedade | Construtor |
|---:|---:|---:|---|---:|---:|
| 1 | 546 ns | 1.305 ns | | 168 B | 288 B |
| 10 | 3,98 µs | 13,8 µs | | 1.176 B | 2.888 B |
| 100 | 37,7 µs | 76,7 µs | | 11.256 B | 28.377 B |
| 1.000 | 244 µs | 618 µs | | 112.056 B | 283.267 B |
| 10.000 | 2,67 ms | 6,51 ms | | 1.120.056 B | 2.832.099 B |

**~2,5x mais caro, no tempo e na alocação**, e por um motivo estrutural:
`ConstructorInfo.Invoke` aloca o vetor de argumentos por linha e não é compilado,
enquanto o caminho de propriedades usa fábrica e setters compilados. É o preço de um
contrato sem propriedade gravável, e é por isso que o construtor sem parâmetros ganha
quando existe — o padrão continua sendo o caminho barato.

> A primeira medição disse o **contrário**, com o construtor alocando 3,8x menos. O
> que a explicava não era o caminho: `DaxValueConverter.Convert` recebia a descrição
> da propriedade (`Tipo.Propriedade`) como argumento, e a interpolava **por célula**
> para compor uma mensagem de erro quase nunca lançada — custo que só o caminho de
> propriedades pagava. Numa leitura de 10.000 linhas com 8 colunas mapeadas eram
> 80.000 strings descartadas, e a materialização por propriedade alocava 605 B por
> linha em vez de 112 B. A checagem de tipo passou para antes da descrição, o que
> devolveu os números da tabela anterior e deixou a comparação medir o que dizia
> medir.

### Achado colateral

`DaxExpressionVisitorBenchmarks.CapturedVariable` declara na documentação que
dispara `Expression.Lambda().Compile().DynamicInvoke()` e que se espera "ordem de
grandeza acima dos demais". Ele mede 963 ns — igual ao baseline de 965 ns.

A expectativa não se confirma porque `EvaluateMember` tem um caminho rápido: numa
variável capturada o `node.Expression` é a `ConstantExpression` do closure e o
membro é um `FieldInfo`, resolvido por `field.GetValue(owner)`. O `Compile()` só
acontece no fallback. A medição, portanto, comprova que o caminho rápido funciona
— e o comentário do benchmark é que está desatualizado.

### Dados completos

Os relatórios de todos os 124 casos (Markdown, HTML, JSON e CSV) são gerados em
`BenchmarkDotNet.Artifacts/results/`, que fica fora do versionamento. Para
comparar antes e depois de uma refatoração, use `--artifacts`:

```bash
dotnet run --project tests/PowerLinq.Benchmark -c Release -- \
    --filter '*Visitor*' --artifacts ./baseline
```

Ver o [README do projeto de benchmark](tests/PowerLinq.Benchmark/README.md) para
os demais filtros e opções.

### Streaming: linhas conforme elas chegam

`ToListAsync` materializa o resultado inteiro antes de o chamador ver a primeira linha. Num
relatório grande sobre tabela fato isso é o pico de memória do processo — e a forma de ler
nunca foi o problema: o executor já lê por `AdomdDataReader`, e o que materializava era o laço
que drenava o reader para dentro de uma lista.

```csharp
await foreach (Produto produto in contexto.Produtos.Where(p => p.Ativo).AsAsyncEnumerable())
{
    // a primeira linha chega antes de a última ser lida
}
```

**É uma fronteira explícita.** Depois daqui não há mais composição: o que volta é uma sequência,
e qualquer filtro ou ordenação passa a acontecer no cliente. Por isso o método tem nome próprio
em vez de a consulta ser enumerável por si.

**Abandonar a enumeração libera a conexão.** Sair por `break` ou por exceção devolve o aluguel ao
pool — quem garante isso é o `await foreach`, chamando `DisposeAsync` do enumerador. Sem isso,
cada requisição interrompida comeria uma conexão do pool até o processo reiniciar.

**Não há retry.** `ToListAsync` repete uma vez quando a sessão cai; aqui não dá: se a falha vier
depois da primeira linha, repetir entregaria de novo linhas que o consumidor já viu. Uma sequência
duplicada em silêncio é pior que uma exceção.

Exige um executor que também implemente `IDaxStreamingQueryExecutor`. `XmlaQueryExecutor` e
`PooledXmlaQueryExecutor` implementam; um executor próprio que não implemente continua servindo
todo o resto, e `AsAsyncEnumerable` recusa nomeando o motivo.

### Contratos a partir do modelo

Escrever `[DaxTable]`/`[DaxColumn]` à mão é escrever uma **afirmação que nada confere**: um erro de
digitação num nome de coluna vira erro do servidor, não do compilador. A saída é inverter a direção —
o modelo vira código:

```csharp
// 1. uma vez, com o endpoint à mão: lê o modelo e grava o esquema
DaxModelSchema esquema = await new DaxSchemaReader(executor).ReadAsync();
File.WriteAllText("modelo.schema.json", DaxSchemaFile.Write(esquema));

// 2. offline, a partir do arquivo: gera os contratos
string contratos = DaxContractGenerator.Generate(
    DaxSchemaFile.Read(File.ReadAllText("modelo.schema.json")), "MinhaApp.Contratos");
```

O gerado traz uma classe por tabela, com os atributos preenchidos do modelo, e as medidas como
**constantes**:

```csharp
[DaxTable("Ordem de Venda")]
public sealed class OrdemDeVenda
{
    [DaxColumn("'Ordem de Venda'[VL_REALIZADO_BRL]")]
    public decimal? VlRealizadoBrl { get; set; }
}

public static class Measures
{
    public const string TotalVendas = "Total Vendas";
}
```

**As constantes são o ponto.** Nome de medida é argumento de `MeasureAsync`, escrito no ponto da
chamada — nenhuma verificação por reflexão o alcança. Com a constante gerada, o erro de digitação
passa a ser erro de **compilação**, que é onde ele deveria morrer.

**A composição continua sem tocar a rede.** É a propriedade preservada ao recusar
validação em execução: `ToDaxString()` segue síncrono e segue não falhando por endpoint fora. A
leitura do modelo é a única peça de rede, e é uma chamada explícita.

**E versionar o esquema tem um efeito que vale de propósito:** a mudança do modelo aparece no
`git diff`. Uma coluna que o time de BI removeu deixa de ser um erro que surge em produção e passa a
ser uma linha vermelha na revisão.

#### A rede de segurança, para contrato escrito à mão

Contrato gerado não pode divergir — o nome veio do modelo. Mas contrato à mão continua existindo, e
contrato gerado envelhece quando o modelo muda depois. Para os dois:

```csharp
IReadOnlyList<DaxDivergence> divergencias =
    DaxContractValidator.Validate(esquema, typeof(MinhaEntidade).Assembly);
```

Ela devolve a lista inteira em vez de lançar na primeira — quem roda isto em CI quer o relatório, não
a primeira linha dele —, e a mensagem palpita:

```
ColumnNotInModel at RealizedRow.Periodo: Table 'FAT_ISENCAO' has no column named
'PERIODO_FECHAMENT'. Did you mean 'PERIODO_FECHAMENTO'?
```

Coluna de **saída** de consulta — `[Realizado]`, sem tabela — não é conferida: ela é extensão de um
`SUMMARIZECOLUMNS`, não coluna do modelo, e acusá-la produziria divergência em todo contrato de
resultado agregado.

`ValidateNavigation` confere relacionamento, e é o que destrava a navegação entre
entidades: ela recusa caminho **ambíguo** nomeando
os dois, distingue caminho **inativo** de caminho inexistente — a saída ali é `USERELATIONSHIP`, não
criar relacionamento —, e recusa a direção que `RELATED` não atravessa, porque ele só cruza
muitos→um.

> **O comando que amarra as peças não existe ainda**, e os nomes das colunas de DMV vêm da
> documentação do TMSCHEMA — eles **não** foram sondados contra um modelo real. O mapeamento é
> testado, a forma do DMV não.

### Consulta compilada: o DAX produzido uma vez

Num painel a **mesma** consulta roda a cada request, com apenas os valores mudando — e cada
execução refaz o caminho inteiro: traduzir os lambdas, dobrar o pipeline em árvore, escrever a
árvore como texto. Uma consulta compilada produz o DAX uma vez e liga os valores depois:

```csharp
private static readonly DaxCompiledQuery<Produto, string> PorCategoria =
    DaxCompiledQuery.Create<Produto, string>(
        (tabela, categoria) => tabela.Where(p => p.Categoria == categoria));

// a cada request
List<Produto> linhas = await contexto.FromCompiledAsync(PorCategoria, "Eletrônicos");
```

| | 1 filtro | 4 filtros |
|---|---:|---:|
| compor a cada execução | 1.207 ns | 4.256 ns |
| ligar os valores no template | 121 ns | 156 ns |
| | **10,0x** | **27,3x** |

O fator não é o número interessante — a **inclinação** é. Ligar valores é praticamente plano entre
1 e 4 filtros, enquanto compor triplica: só a tradução escala com o número de operadores, e é ela
que a consulta compilada deixa de pagar.

**O parâmetro é declarado, não descoberto, e é essa a garantia.** `DaxParameter<T>` é um marcador
**sem valor** — `Value` e a conversão implícita lançam. Elas existem para dar a sintaxe natural
(`p.Categoria == categoria`), e o tradutor as reconhece pela *forma da expressão*, antes de avaliar
nada. No momento em que o DAX é produzido o valor não existe, então não tem como ser gravado no
template. Um cache que adivinhasse o que é parâmetro poderia devolver o DAX de outro filtro — um
erro plausível o suficiente para passar em revisão; aqui isso não é improvável, é inexpressável.
Se algum caminho não reconhecido chegar a ler o valor, o resultado é exceção imediata.

**Não há tabela de cache dentro da biblioteca.** O template mora na própria consulta compilada, que
você guarda. Não há política de expiração a definir nem teto a configurar — cache sem teto dentro de
uma biblioteca é vazamento de memória no processo de quem a consome, e a forma de não ter esse
problema é não ter a tabela. Não usar é o desligar.

**Só valor é parametrizável.** `Take` e `Skip` recebem `int` e não passam pelo tradutor: eles mudam
a forma da consulta, então pedem uma consulta compilada por tamanho de janela. Nome de coluna e
operador, pela mesma razão, não são valores. E composição dinâmica — `WhereIf` com filtro opcional
— produz uma consulta diferente por combinação, então quem quer o ganho ali declara uma consulta
compilada por combinação, ou aceita compor.

### Saída de emergência: DAX escrito à mão

Uma biblioteca de tradução acaba precisando de uma saída, e a pergunta não é se ela existe — é se
está **declarada** ou reinventada. Antes disso, ler uma medida passava por
`ExecuteCountAsync`, um método cujo nome diz "contagem": o desvio existia, sem nome e sem
documentação, e ninguém que lesse a chamada perceberia.

```csharp
List<Linha> linhas = await contexto.FromRawDaxAsync<Linha>(dax);
decimal? valor   = await contexto.FromRawDaxScalarAsync<decimal?>(dax);
```

O nome é longo de propósito: a chamada denuncia o que ela é.

**O que se perde** — tudo o que a tradução garante. Não há verificação de coluna contra o
contrato, não há distinção entre medida e coluna, não há resolução de ordenação contra o
resultado, e erro de sintaxe só aparece no servidor. A composição também se perde: o resultado é
uma lista, não uma consulta.

**O que se mantém** — a materialização é a mesma de todo o resto, pelo `EntityMapper`: os mesmos
atributos, a mesma tabela de tipos, a mesma conversão em cultura invariante, o mesmo erro nomeando
propriedade e coluna. Duplicar isso aqui criaria duas listas de "o que é suportado" que divergiriam
na primeira vez que uma ganhasse um tipo novo.

O texto vai **como veio** — sem normalizar, sem reescrever. A única verificação é recusar consulta
vazia; validar mais significaria reimplementar o analisador do servidor, e a razão de existir desta
porta é aceitar o que a tradução não expressa.

## Limitações

Conhecidas e verificadas nesta versão:

**O agrupamento não pode vir depois de uma janela.** `GroupBy` e `Aggregate` depois de
`Take` ou `Skip` lançam `NotSupportedException`:

```csharp
tabela.Take(5).GroupBy(p => p.Nome)                   // NotSupportedException
tabela.OrderBy(p => p.Id).Skip(10).Aggregate(...)     // NotSupportedException
```

O motivo não é estrutural: o `SUMMARIZECOLUMNS` recebe as colunas de agrupamento e as
tabelas de filtro, e **não tem onde receber um `TOPN`** — então agrupar depois de uma
janela a descartaria em silêncio, e a agregação varreria a tabela inteira.

Declarar a janela em `VAR` é o que abre caminho, e esse mecanismo já existe — hoje ele é
usado na paginação, onde a fonte aparece duas vezes.
Passar a janela declarada como argumento de tabela de filtro do `SUMMARIZECOLUMNS` é o
passo que falta, e ele **não** foi dado: o operador continua recusado.

Os demais operadores **não** têm mais ordem fixa. Antes, a composição só podia seguir
`Where` → `OrderBy` → `Skip` → `Take`, porque a representação interna guardava campos
independentes sem registrar a ordem — `Take(5).Where(p)` e `Where(p).Take(5)` eram o
mesmo valor, e recusar era preferível a gerar o DAX de um quando se pediu o outro. Agora
cada ordem tem a sua tradução:

```csharp
tabela.Where(p => p.Ativo).Take(5)   // TOPN(5, FILTER(Produto, ...))  — filtra, depois limita
tabela.Take(5).Where(p => p.Ativo)   // FILTER(TOPN(5, Produto), ...)  — limita, depois filtra
tabela.Take(5).Take(10)              // TOPN(10, TOPN(5, ...))         — 5 linhas, como no LINQ
tabela.Take(5).OrderBy(p => p.Nome)  // TOPN(5, ...) ORDER BY ...      — ordena a janela
```

**`OrderBy` substitui a ordenação; `ThenBy` acrescenta.** Como no LINQ:

```csharp
.OrderBy(p => p.Id).OrderBy(p => p.Nome)   // ORDER BY Produto[Nome] ASC
.OrderBy(p => p.Id).ThenBy(p => p.Nome)    // ORDER BY Produto[Id] ASC, Produto[Nome] ASC
```

**A ordenação precisa sobreviver à projeção.** Ordenar antes de um `Select` ou de um `Join`
só funciona quando a coluna é levada para o resultado — o DAX só garante a ordem da saída pela
cláusula `ORDER BY` do `EVALUATE`, e ela só pode referenciar colunas que o resultado tem:

```csharp
.OrderByDescending(p => p.Preco).Select(p => new R { Nome = p.Nome, Preco = p.Preco })  // ok
.OrderByDescending(p => p.Preco).Select(p => new R { Nome = p.Nome })  // NotSupportedException
```

No primeiro caso a ordenação é **reescrita** para `[Preco]`, a coluna de saída correspondente.
Incluir a coluna sozinho não é opção do tradutor: acrescentar ao resultado algo que não foi
pedido, só para poder ordená-lo, muda o que a consulta devolve.

**Ordenar por expressão também atravessa a projeção.** Cada referência de coluna dentro
da expressão é reescrita para a coluna de saída correspondente, e basta **uma** que a reforma não
leve adiante para o termo inteiro ser recusado — nomeando a coluna que falta, não "a expressão":

```csharp
// Período "MM-yyyy": ordenar o texto agrupa por mês. A chave é ano * 100 + mês, e ela
// sobrevive à projeção de uma coluna do DistinctValuesAsync.
tabela
    .OrderByDescending(x => int.Parse(x.Periodo.Substring(3)) * 100
                            + int.Parse(x.Periodo.Substring(0, 2)))
    .DistinctValuesAsync(x => x.Periodo);
// EVALUATE DISTINCT(SELECTCOLUMNS(Fato, "Value", Fato[Periodo]))
// ORDER BY VALUE(MID([Value], 4, LEN([Value]))) * 100 + VALUE(MID([Value], 1, 2)) DESC
```

A reescrita cobre referência de coluna, literal, operador, `NOT`, `IN` e chamada de função sobre
esses. O que é resolvido **por nome** — medida do modelo, `VAR` do bloco `DEFINE` — é recusado em
vez de adivinhado: esses nomes não vêm do resultado da reforma, e deixá-los passar geraria DAX que
o servidor recusa, ou pior, que ele aceita respondendo outra pergunta.

**Num `GroupBy` a ordenação continua tendo de ser por chave.** Não é limitação da reescrita: a
agregação colapsa as linhas do grupo, então não sobra um valor por linha de saída para a expressão
avaliar. Ordenar pelo valor agregado — pela coluna de extensão — é outro caso, e funciona.

**Depois de uma reforma, a coluna é resolvida contra o resultado.** `Select`,
`GroupBy`/`Aggregate` e `Join` trocam a forma das linhas: o resultado passa a carregar um
conjunto **fechado** de colunas — as de extensão como `[Total]`, as de chave qualificadas
pela tabela. `Where`, `OrderBy` e a chave de um `Join` seguinte resolvem contra esse
conjunto:

```csharp
tabela
    .GroupBy(v => v.Categoria)
    .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
    .Where(r => r.Total > 1000m)          // FILTER(SUMMARIZECOLUMNS(...), [Total] > 1000)
    .OrderByDescending(r => r.Total)
    .Take(10);
```

O `Where` depois de agregar é o `HAVING` do SQL. Referenciar coluna que o resultado não
carrega lança `NotSupportedException` **nomeando as que ele carrega** — a propriedade existe
no tipo de resultado e compila, então sem a lista não há como descobrir o motivo.

O que **não** se compõe depois de uma reforma são os agregados escalares (`SumAsync` e
companhia) e um `GroupBy` sobre resultado já agregado.

**Os dois lados do `Join` podem vir filtrados**, e a chave pode ser composta:

```csharp
vendas.Where(v => v.Ativo)
    .Join(
        clientes.Where(c => c.Uf == "SP"),
        v => new { v.ClienteId, v.Ano },
        c => new { ClienteId = c.Id, c.Ano },
        (v, c) => new Resultado { Valor = v.Valor, Cliente = c.Nome });
```

O lado interno aceita **apenas filtro**: ele entra como a tabela a cruzar, e ordená-lo ou
limitá-lo antes do cruzamento mudaria quais linhas participam dele sem que o `GENERATE`
expresse isso. Ordenar ou limitar o **resultado** do join funciona, e encadear um join sobre
outro também.

A projeção aceita expressão calculada, desde que ela toque **um** lado só:

```csharp
(v, c) => new R { Valor = v.Valor * 1.1m, Cliente = c.Nome.ToUpper() }   // ok
(v, c) => new R { Etiqueta = c.Nome + v.Id }                             // NotSupportedException
```

Cada coluna de saída é calculada dentro do lado dela, **antes** do `GENERATE`, e ali cada lado
só tem as próprias colunas. Para combinar os dois, projete-os separadamente e combine num
`Select` depois do join.

**Ordenar resultado agregado só funciona por coluna de chave.** `OrderBy` antes de
`GroupBy` chega ao DAX como `ORDER BY` quando pede uma coluna que é chave do
agrupamento. Por coluna que **não** é chave, é recusado — e não por limitação de
implementação: a agregação colapsa as linhas do grupo, então não sobra um valor
daquela coluna por linha de saída para ordenar.

```csharp
.OrderBy(v => v.Categoria).GroupBy(v => v.Categoria)   // ok, ORDER BY Venda[Categoria] ASC
.OrderByDescending(v => v.Valor).GroupBy(v => v.Categoria)   // NotSupportedException
```

Ordenar pelo **valor agregado** funciona ordenando **depois** do agrupamento, pela coluna de
extensão — que é o Top N por valor:

```csharp
await tabela
    .GroupBy(v => v.Categoria)
    .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
    .OrderByDescending(r => r.Total)
    .Take(10)
    .ToListAsync();

// EVALUATE TOPN(10, SUMMARIZECOLUMNS(Venda[Categoria], "Total", SUM(Venda[Valor])),
//               [Total], DESC)
```

Depois de agregar, projetar ou juntar, o resultado carrega um conjunto **fechado** de colunas
— as chaves qualificadas pela tabela (`Venda[Categoria]`) e as extensões entre colchetes
(`[Total]`) — e a ordenação é resolvida contra ele. Ordenar por coluna que o resultado não
carrega lança `NotSupportedException` **nomeando as que ele carrega**.

**A superfície `IQueryable` cobre um subconjunto dos operadores.** Traduzem `Where`,
`OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending`, `Skip`, `Take`, `Distinct` e
`Select`. O resto é recusado nomeando o operador — inclusive `GroupBy` e `Join`, que
existem na API fluente e continuam só lá:

```csharp
produtos.GroupBy(p => p.Categoria)              // NotSupportedException, aponta a fluente
produtos.Select(p => p.Nome)                    // NotSupportedException, aponta ValuesAsync
produtos.Where((p, indice) => indice < 5)       // NotSupportedException: não há posição em DAX
```

A razão de `GroupBy` não estar ali não é esforço: a agregação desta biblioteca tem forma
própria — `IDaxGroup`, com `g.Measure(...)` para medida do modelo e coluna de extensão
nomeada —, e `IGrouping` do LINQ não expressa isso. Traduzir os dois para o mesmo estágio é
trabalho por si, não consequência do provider.

A projeção escalar é recusada porque a materialização monta objetos: `Select(p => p.Nome)`
devolveria `IQueryable<string>`, e uma coluna solta não tem contrato onde pousar —
`ValuesAsync` e `DistinctValuesAsync` são os terminais para isso.

**Não há enumeração síncrona no `IQueryable`.** `foreach`, `ToList()`, `Count()` e os
demais terminais do LINQ lançam `NotSupportedException` nomeando a forma assíncrona. É
decisão, não pendência: bloquear uma thread numa ida à rede esgota o pool sob carga. Nenhum
caminho cai em avaliação no cliente — a fronteira é `ToListAsync` ou `AsAsyncEnumerable`, e
depois dela vale o LINQ a objetos.

**O contexto não rastreia alterações.** `DaxContext` segue o padrão de DI e
unidade de acesso do EF Core, mas o PowerLinq é somente leitura: não há change
tracking, `SaveChanges` nem comandos de escrita.

No `Join`, as chaves devem ser propriedades simples e o resultado deve usar
atribuições diretas das duas entidades.

**Membro aninhado e alguns métodos não são traduzidos.** A regra é o que a
expressão depende: o que **não** depende do parâmetro do lambda é avaliado na
tradução e vira literal; o que depende precisa ter tradução, ou é recusado com
`NotSupportedException` nomeando o membro.

```csharp
// Não dependem do parâmetro — avaliados na tradução:
p => p.Data >= new DateTime(2024, 1, 1)   // Produto[Data] >= DATE(2024,1,1)
p => p.Data >= DateTime.Today.AddDays(-30)
p => p.Preco > decimal.Parse(texto)

var inicio = new DateTime(2024, 1, 1);
p => p.Data >= inicio                     // idem, variável capturada

// Dependem do parâmetro e ainda não têm tradução — recusados:
p => p.Data.DayOfYear > 3            // "Produto.Data.DayOfYear has no DAX translation"
p => p.Data.Ticks > 0                // "Produto.Data.Ticks has no DAX translation"
p => p.Nome.PadLeft(5) == "A"        // método sem tradução
```

`DayOfYear` e `Ticks` não têm equivalente direto em DAX; `PadLeft` exigiria compor
`REPT` com o operador de concatenação, que ainda não é emitido.

**`Abstractions` e `Cache` estão vazios.** São esqueletos de projeto sem
implementação.

**A entidade precisa ser uma classe**, e a restrição é `where T : class`, de modo que
`struct` **não compila**. Antes compilava e a materialização devolvia tudo com valor
default: a entidade era encaixotada na fronteira da atribuição, a escrita acontecia na
caixa e a caixa era descartada no retorno. Falha silenciosa — o DAX certo, a contagem
de linhas certa, e todos os valores zero. `record` funciona (é classe); `record struct`
e `readonly record struct` não.

Os dois caminhos de reflexão que dominavam os hot paths **já** são cacheados: a
resolução de `[DaxColumn]` por `PropertyInfo` na tradução (eliminou os 778 ns, ver
[Custo de um predicado simples](#custo-de-um-predicado-simples)) e o mapeamento de
colunas por tipo na materialização, agora com setter compilado no lugar de
`PropertyInfo.SetValue` (ver [Materialização](#materialização)).

## Desenvolvimento

```bash
dotnet build PowerLinq.slnx -c Release
dotnet test tests/PowerLinq.Tests/PowerLinq.Tests.csproj -c Release
```

O `global.json` pede o SDK **10.0.100** com `rollForward: latestFeature`, então
qualquer .NET 10 serve — a banda de features não importa. SDK de pré-lançamento
é recusado (`allowPrerelease: false`).

Os analisadores do .NET e as regras de estilo do `.editorconfig` valem no build.
Localmente eles aparecem como aviso; **no CI o build usa `-warnaserror`**, então
nada com aviso chega a `master`. Antes de abrir PR, vale rodar como o CI roda:

```bash
dotnet build PowerLinq.slnx -c Release -warnaserror
```

O [CONTRIBUTING](CONTRIBUTING.md) detalha o ciclo de trabalho, as convenções de
commit e branch, como comparar benchmarks antes e depois de uma mudança, e as
armadilhas já registradas — de medição de performance a propriedades de MSBuild
que o SDK consome cedo demais. As mudanças ficam em
[CHANGELOG.md](CHANGELOG.md).

Benchmarks de performance ficam em `tests/PowerLinq.Benchmark` — ver o
[README de lá](tests/PowerLinq.Benchmark/README.md) para como rodar e comparar
antes/depois de uma refatoração.

## Licença

MIT. Ver [LICENSE](LICENSE).

Uso, cópia, modificação e redistribuição são livres, desde que o aviso de
copyright e o texto da licença acompanhem as cópias. As dependências de
terceiros (ADOMD.NET, `Azure.Identity`, `Microsoft.Extensions.*`) seguem as
próprias licenças, listadas no fim do arquivo — em particular o ADOMD.NET é
proprietário da Microsoft e não é coberto pela licença deste repositório.

Os pacotes declaram a licença por `PackageLicenseExpression` (`MIT`): o
identificador SPDX viaja no nuspec e o NuGet mostra a licença sem exigir aceite
na instalação. O arquivo `LICENSE` continua dentro do `.nupkg` como conteúdo.

## Distribuição

**A publicação automática no NuGet.org público segue desativada.** A licença
deixou de ser o impedimento — MIT concede o direito de usar o que for publicado
—, mas o gatilho continua manual enquanto a API está em pré-lançamento.

O workflow `Publish NuGet packages` segue íntegro, mas o disparo automático por
tag `v*` foi removido, para que uma tag de release não publique por acidente.
Hoje ele só roda por acionamento manual (`workflow_dispatch`), informando a
versão sem o prefixo `v`; somente versões SemVer de pré-lançamento são aceitas
nesta fase.

Para publicar por tag de novo, basta restaurar o gatilho. A infraestrutura já
está pronta e usa
Publicação Confiável por OpenID Connect, então nenhuma API key de longa duração
precisa ser criada ou armazenada no GitHub. Restaria apenas:

1. criar o ambiente `nuget` no repositório GitHub;
2. cadastrar `NUGET_USER` como variável desse ambiente, com o nome do perfil no
   NuGet.org (não o e-mail);
3. no NuGet.org, criar a política de Publicação Confiável com proprietário
   `Jean-Ruffato`, repositório `PowerLinq`, workflow `publish-nuget.yml` e
   ambiente `nuget`.
