# PowerLinq.DaxConverter

Provider LINQ para DAX que converte expressões C# em consultas DAX tipadas e
materializa os resultados em objetos .NET.

> Pacote em pré-lançamento, sob licença MIT. A API ainda pode mudar antes da
> versão estável.

## Licença e distribuição

Licença MIT — ver o arquivo `LICENSE`, incluído no pacote. A publicação
automática no NuGet.org público ainda não está ligada enquanto a API está em
pré-lançamento.

Este pacote contém o tradutor e as abstrações de contexto. Para executar
consultas em endpoints XMLA, registre um `IDaxQueryExecutor`, como o fornecido
por `PowerLinq.ConnectionPool`.

## Uso rápido

Mapeie a tabela e suas colunas:

```csharp
using PowerLinq.DaxConverter.Attributes;

[DaxTable("Produto")]
public sealed class Produto
{
    [DaxColumn("Produto[ProdutoID]")]
    public int Id { get; set; }

    [DaxColumn("Produto[Nome]")]
    public string Nome { get; set; } = "";

    [DaxColumn("Produto[Preco]")]
    public decimal Preco { get; set; }
}
```

Defina um contexto que dependa das abstrações do PowerLinq:

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

O pacote de conexão registra o contexto e o executor no contêiner de DI. Depois,
injete o contexto no serviço e componha a consulta:

```csharp
public sealed class ProdutoService(CatalogoContext context)
{
    public async Task<List<Produto>> ListarAsync()
    {
        var consulta = context.Produtos
            .Where(x => x.Preco > 100m)
            .OrderByDescending(x => x.Preco)
            .Take(10);

        Console.WriteLine(consulta.ToDaxString());
        return await consulta.ToListAsync();
    }
}
```

As mensagens da biblioteca ficam em recursos `.resx`. Ao usar
`PowerLinq.ConnectionPool`, importe `PowerLinq.Extensions.DependencyInjection`,
passe `IConfiguration` para `AddDaxContext` e defina opcionalmente
`PowerLinq:Localization:Language` como `pt-BR`. Inglês é o idioma padrão e o
fallback para valores não suportados. O localizador é injetável e não altera a
cultura global da aplicação.

O DAX é gerado somente no fim da composição:

```dax
EVALUATE
TOPN(
    10,
    FILTER(
        Produto,
        Produto[Preco] > 100
    ),
    Produto[Preco], DESC
)
```

## Recursos disponíveis

- filtros com `Where` e `WhereIf`;
- ordenação com `OrderBy`, `OrderByDescending`, `ThenBy` e `ThenByDescending`;
- limitação com `Take`;
- paginação ordenada com `Skip` e `Take`;
- execução com `ToListAsync`, `ToArrayAsync`, `ToDictionaryAsync`, `FirstAsync`,
  `FirstOrDefaultAsync`, `SingleAsync`, `SingleOrDefaultAsync`, `CountAsync`,
  `LongCountAsync`, `AnyAsync` e `AllAsync`;
- agregados escalares diretos com `SumAsync`, `MinAsync`, `MaxAsync`, `AverageAsync` e
  `AverageOrDefaultAsync`, emitidos na forma iteradora (`SUMX`) sobre a fonte filtrada —
  a forma escalar (`SUM`) somaria no contexto de filtro corrente e ignoraria o `Where`;
- equijoin tipado com `Join`, com filtro nos **dois** lados e chave composta por
  `new { a, b }`; o lado interno aceita só filtro, porque entra como a tabela a cruzar;
- projeção para DTOs e contratos com `Select`/`SELECTCOLUMNS`;
- deduplicação no servidor com `Distinct()`, e os valores de uma coluna com
  `ValuesAsync(x => x.Coluna)` ou `DistinctValuesAsync(x => x.Coluna)` — sem contrato
  intermediário;
- agrupamentos e agregações com `GroupBy`, `Aggregate`, `Sum`, `SumWhere` e `Count`;
- inspeção sem execução com `ToDaxString()` e `ToSyntaxTree()`;
- mapeamento convencional ou explícito por `[DaxTable]` e `[DaxColumn]`;
- `IQueryable` como superfície adicional, por `AsQueryable()`, para sintaxe de query e para
  entregar a consulta a código que só conhece `IQueryable<T>`.

## Projeção para contratos

`Select` traz somente as colunas necessárias e materializa diretamente o tipo de
resposta. As propriedades do contrato são mapeadas por nome; `[DaxColumn]` é
opcional quando for necessário alterar o alias.

```csharp
public sealed class ProdutoResponse
{
    public int Codigo { get; set; }
    public string Descricao { get; set; } = "";
    public decimal PrecoComTaxa { get; set; }
}

var resposta = await context.Produtos
    .Where(x => x.Ativo)
    .Select(x => new ProdutoResponse
    {
        Codigo = x.Id,
        Descricao = x.Nome,
        PrecoComTaxa = x.Preco * 1.1m
    })
    .ToListAsync();
```

A consulta gera `SELECTCOLUMNS` no servidor, inclusive para expressões escalares
suportadas pelo tradutor.

Operadores em predicados: `==`, `!=`, `>`, `>=`, `<`, `<=`, `&&`, `||`, `!`,
`+`, `-`, `*` e `/`. Também são convertidos `null`, `bool`, `DateTime` e valores
capturados em variáveis.

Para texto, são suportados `Contains`, `StartsWith`, `EndsWith`, `ToUpper`,
`ToLower`, `Trim`, `string.IsNullOrEmpty` e `string.IsNullOrWhiteSpace`. A
comparação segue a collation configurada no modelo DAX e, portanto, pode diferir
da comparação ordinal do .NET.

## Limitações atuais

A composição **não** tem ordem fixa: cada operador é um estágio na sequência em que
foi aplicado, então `Where(p).Take(5)` gera `TOPN(5, FILTER(...))` e `Take(5).Where(p)`
gera `FILTER(TOPN(5, ...), ...)` — duas consultas diferentes, como no LINQ.

Três restrições restam. `GroupBy` e `Aggregate` **não** podem vir depois de `Take` ou
`Skip`, porque o `SUMMARIZECOLUMNS` não tem onde receber a janela e a descartaria em
silêncio. Ordenar antes de um `Select` ou de um `Join` exige que a coluna seja levada para o
resultado, porque o `ORDER BY` do `EVALUATE` só referencia colunas que o resultado tem — quando
ela é levada, a ordenação é reescrita para a coluna de saída. E **depois** de uma reforma
(`Select`, `GroupBy`/`Aggregate`, `Join`) a coluna é resolvida contra o conjunto fechado que o
resultado carrega: `Where`, `OrderBy` e a chave de um `Join` seguinte funcionam ali; os
agregados escalares e um `GroupBy` sobre resultado já agregado, não.

`Skip` exige um `OrderBy`, pois paginação sem ordem não é determinística. `Join`
aceita chave simples ou composta, filtro nos dois lados, expressão calculada na projeção — desde
que ela toque um lado só — e encadeamento. Expressão que combina colunas dos **dois** lados é
recusada: cada coluna de saída é calculada dentro do lado dela, antes do `GENERATE`. Combine-as
num `Select` depois do join. Também não
há suporte a todos os métodos de string, como `Substring`.

Pela superfície `IQueryable`, traduzem `Where`, `OrderBy`, `OrderByDescending`, `ThenBy`,
`ThenByDescending`, `Skip`, `Take`, `Distinct` e `Select`; `GroupBy`, `Join` e os agregados de
grupo continuam só na API fluente, porque a forma deles é específica do DAX e `IGrouping` não a
expressa. Não há enumeração síncrona: `foreach` e `Count()` lançam nomeando o terminal
assíncrono, porque cada terminal é uma ida ao endpoint XMLA. Consulte o
[repositório do projeto](https://github.com/Jean-Ruffato/PowerLinq) para exemplos,
status e documentação de desenvolvimento.

## Requisitos

- .NET 10 ou superior.
