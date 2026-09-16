# Breaking long parenthesized expressions

The general rule: break at the points of lowest precedence/grouping, and align so the structure stays visible. Joining operators go at the **start** of the continuation line.

## Method call with many arguments

One argument per line, indented one level from the call. Closing paren next to the last argument (most common in .NET) or on its own line aligned to the statement start — pick one and standardize via `.editorconfig`.

```csharp
var resultado = calculadora.CalcularImposto(
    valorBase,
    aliquota,
    descontos,
    isento: false);
```

**Once you break the call, break it all the way.** Breaking after the opening paren and then packing every argument onto one continuation line is not a valid middle ground — it pays the vertical cost of the break without buying the per-argument readability, and it produces a line that will be re-broken the moment one argument grows:

```csharp
// Wrong — broke the line, kept the arguments crammed
var keyColumns = DaxAggregationTranslator.TranslateKey(
    _keySelector.Body, _definition.TableName, _localizer);

// Right — one argument per line
var keyColumns = DaxAggregationTranslator.TranslateKey(
    _keySelector.Body,
    _definition.TableName,
    _localizer);
```

The decision is binary: either the whole call fits on one line and stays there, or it breaks with one argument per line. The same applies to method/constructor/record declarations.

An argument that is itself a nested call still counts as **one** argument — it gets its own line and may break internally:

```csharp
columns.Add(new DaxColumnRef(
    DaxExpressionVisitor.ResolveColumnReference(projection.SourceProperty, tableName)));
```

## Blank line after a multi-line statement

Breaking a statement across lines costs you the statement boundary: in a run of indented continuation lines, nothing marks where one statement ends and the next begins. Put a blank line after every multi-line statement to give it back.

```csharp
// Hard to scan — where does each statement end?
var outerSelect = new DaxTableFunctionCall(
    "SELECTCOLUMNS",
    [new DaxTableRef(outer.Definition.TableName), .. outerColumns]);
var innerSelect = new DaxTableFunctionCall(
    "SELECTCOLUMNS",
    [new DaxTableRef(inner.Definition.TableName), .. innerColumns]);
var keysEqual = new DaxBinary(
    DaxOperator.Equal,
    new DaxColumnRef($"[{outerKeyAlias}]"),
    new DaxColumnRef($"[{innerKeyAlias}]"));

// Each statement reads as one unit
var outerSelect = new DaxTableFunctionCall(
    "SELECTCOLUMNS",
    [new DaxTableRef(outer.Definition.TableName), .. outerColumns]);

var innerSelect = new DaxTableFunctionCall(
    "SELECTCOLUMNS",
    [new DaxTableRef(inner.Definition.TableName), .. innerColumns]);

var keysEqual = new DaxBinary(
    DaxOperator.Equal,
    new DaxColumnRef($"[{outerKeyAlias}]"),
    new DaxColumnRef($"[{innerKeyAlias}]"));
```

Scope of the rule:
- It triggers on the statement **breaking**, not on its length — a long single-line statement groups freely with its neighbours.
- It applies to any multi-line statement: broken calls, object/collection initializers, long ternaries, fluent chains, multi-line conditions.
- It holds inside blocks too (`foreach`, `try`, `if`), not just at method top level.
- Members already receive a blank line from the one-blank-line-between-members rule; nothing extra is needed there.
- The trailing blank line is not needed when the statement is the last one in its block — the closing brace already ends it.

## Method declaration

Same logic — one parameter per line, all-or-nothing:

```csharp
public async Task<PedidoResult> ProcessarPedidoAsync(
    Guid pedidoId,
    ClienteInfo cliente,
    IReadOnlyList<Item> itens,
    CancellationToken cancellationToken)
{
    // ...
}
```

## Complex boolean / conditional expressions

Operator (`&&`/`||`) at the start of each line — makes the relationship obvious before the operand. Keep parenthesized subgroups on one line when they fit.

```csharp
if (pedido.Ativo
    && pedido.Valor > limiteMinimo
    && (cliente.Premium || cliente.Antiguidade > 5)
    && !pedido.Cancelado)
{
    // ...
}
```

If it's still hard to read, extract to named variables — almost always beats any formatting:

```csharp
bool valorAcimaDoMinimo = pedido.Valor > limiteMinimo;
bool clienteElegivel = cliente.Premium || cliente.Antiguidade > 5;

if (pedido.Ativo && valorAcimaDoMinimo && clienteElegivel && !pedido.Cancelado)
{
    // ...
}
```

## Arithmetic with nested parentheses

Break at the lowest-precedence operators; indentation reflects nesting; each parenthesized group visually isolated.

```csharp
var total =
    (precoUnitario * quantidade)
    + (precoUnitario * quantidade * taxaImposto)
    - descontoAplicado;
```

## Long ternary

Condition on the first line, `?` and `:` starting the continuation lines:

```csharp
var mensagem = pedido.Aprovado
    ? "Pedido confirmado com sucesso"
    : "Pedido pendente de análise";
```

## Fluent (chained) calls

One `.Metodo(...)` per line, the dot starting the line. When a lambda inside the parentheses is long, break it internally too, keeping indentation consistent.

```csharp
var query = context.Pedidos
    .Where(p => p.Data >= inicio && p.Data <= fim)
    .GroupBy(p => p.ClienteId)
    .Select(g => new
    {
        ClienteId = g.Key,
        Total = g.Sum(p => p.Valor),
    });
```

## Cross-cutting

- Operator-first (start of line) applies to `&&`, `||`, `+`/`-`/`*`, `?`/`:`, and the `.` of fluent chains.
- The VS formatter / `dotnet format` already handles most of this. What it does *not* do — and where the real readability gain is — is deciding to *extract* subexpressions into named variables or methods.
