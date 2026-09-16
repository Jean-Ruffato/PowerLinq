# LINQ formatting

A LINQ operator with a lot going on usually falls into one of these cases. The strategy depends on *what* is inflating the line.

## The lambda itself is long (many conditions)

Don't cram everything inside the parentheses. Break the lambda body (operator-first):

```csharp
var elegiveis = pedidos
    .Where(p =>
        p.Ativo
        && p.Valor > limiteMinimo
        && (p.Cliente.Premium || p.Cliente.Antiguidade > 5)
        && !p.Cancelado)
    .ToList();
```

Better — extract to a named method; the predicate becomes self-explanatory and testable:

```csharp
var elegiveis = pedidos
    .Where(EhElegivel)
    .ToList();

// ...
static bool EhElegivel(Pedido p) =>
    p.Ativo
    && p.Valor > limiteMinimo
    && (p.Cliente.Premium || p.Cliente.Antiguidade > 5)
    && !p.Cancelado;
```

## `Select` projects many properties

One assignment per line, trailing comma (eases diffs and reordering):

```csharp
var dtos = pedidos
    .Select(p => new PedidoDto
    {
        Id = p.Id,
        Cliente = p.Cliente.Nome,
        Total = p.Itens.Sum(i => i.Valor),
        Data = p.DataCriacao,
        Status = p.Status.ToString(),
    })
    .ToList();
```

## Overloads with several arguments (`GroupBy`/`Aggregate`/`Join`)

Some LINQ methods genuinely take multiple positional arguments. One argument per line, each lambda as a block. Use **named arguments** when the overloads are ambiguous or the lambdas alone don't make each role clear.

```csharp
var resumo = pedidos.Aggregate(
    seed: new Resumo(),
    func: (acc, p) =>
    {
        acc.Total += p.Valor;
        acc.Quantidade++;
        return acc;
    },
    resultSelector: acc => acc with { Media = acc.Total / acc.Quantidade });
```

`GroupBy` with three selectors is the classic case where naming saves the read:

```csharp
var agrupado = pessoas.GroupBy(
    keySelector: p => p.Departamento,
    elementSelector: p => p.Nome,
    resultSelector: (depto, nomes) => new
    {
        Departamento = depto,
        Pessoas = nomes.ToList(),
    });
```

## Short chain that fits the line

If it fits comfortably and the chain is short (2–3 trivial operators), keep it on one line — forcing a break adds vertical noise:

```csharp
var nomes = pedidos.Where(p => p.Ativo).Select(p => p.Nome).ToList();
```

At ~4+ operators, or with any non-trivial lambda, switch to fluent (one `.Operator(...)` per line) even if it fits, so each transformation reads in isolation. The line-length limit is a ceiling, not a target.

## Cross-cutting rule

If a single operator is hard to format, it's usually doing too much. The real fix is to **split the query into named steps** or extract lambdas — not to hunt for perfect indentation. Deferred execution is preserved (nothing materializes until `ToList`/`foreach`), so splitting costs nothing:

```csharp
var ativos = pedidos.Where(p => p.Ativo);
var doPeriodo = ativos.Where(p => p.Data >= inicio && p.Data <= fim);
var resumo = doPeriodo.GroupBy(p => p.ClienteId).Select(MontarResumo);
```

Keep a chain fluent when it reads as one unit; split into named variables when the steps have meaning on their own.
