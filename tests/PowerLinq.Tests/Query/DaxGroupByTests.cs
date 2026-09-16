using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

public class DaxGroupByTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("Venda[Ano]")] public int Ano { get; set; }
        [DaxColumn("Venda[Valor]")] public double? Valor { get; set; }
        [DaxColumn("Venda[Custo]")] public double? Custo { get; set; }
    }

    // Key through [DaxColumn(real column)]; aggregations through [DaxColumn("[name]")].
    private sealed class PorCategoria
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("[Total]")] public double? Total { get; set; }
        [DaxColumn("[Qtd]")] public long Qtd { get; set; }
    }

    private sealed class NoopExecutor : IDaxQueryExecutor
    {
        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class => Task.FromResult(new List<T>());

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private static DaxTable<Venda> Table() => new(new NoopExecutor());

    [Fact]
    public void GroupBy_SumAndCount_ProducesSummarizeColumns()
    {
        string dax = Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Categoria = g.Key,
                Total = g.Sum(x => x.Valor),
                Qtd = g.Count()
            })
            .ToDaxString();

        Assert.StartsWith("EVALUATE", dax);
        Assert.Contains("SUMMARIZECOLUMNS(", dax);
        Assert.Contains("Venda[Categoria]", dax);
        Assert.Contains("\"Total\", SUM(Venda[Valor])", dax);
        Assert.Contains("\"Qtd\", COUNTROWS(Venda)", dax);
    }

    [Fact]
    public void GroupBy_WithWhere_EmitsFilterArgument()
    {
        string dax = Table()
            .Where(v => v.Ano == 2024)
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Categoria = g.Key,
                Total = g.Sum(x => x.Valor),
                Qtd = g.Count()
            })
            .ToDaxString();

        Assert.Contains("FILTER(Venda, Venda[Ano] = 2024)", dax);
    }

    [Fact]
    public void GroupBy_SumOfColumns_CombinesWithPlus()
    {
        string dax = Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Categoria = g.Key,
                Total = g.Sum(x => x.Valor) + g.Sum(x => x.Custo),
                Qtd = g.Count()
            })
            .ToDaxString();

        Assert.Contains("SUM(Venda[Valor]) + SUM(Venda[Custo])", dax);
    }

    [Fact]
    public void GroupBy_SumOfPerRowExpression_UsesSumx()
    {
        string dax = Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Categoria = g.Key,
                Total = g.Sum(x => x.Valor - x.Custo),
                Qtd = g.Count()
            })
            .ToDaxString();

        Assert.Contains("SUMX(Venda, Venda[Valor] - Venda[Custo])", dax);
    }

    private sealed class Totais
    {
        [DaxColumn("[Total]")] public double? Total { get; set; }
        [DaxColumn("[Qtd]")] public long Qtd { get; set; }
    }

    [Fact]
    public void Aggregate_NoKey_ProducesSummarizeColumnsWithoutGroupColumn()
    {
        string dax = Table()
            .Where(v => v.Ano == 2024)
            .Aggregate(g => new Totais { Total = g.Sum(x => x.Valor), Qtd = g.Count() })
            .ToDaxString();

        Assert.Contains("SUMMARIZECOLUMNS(", dax);
        Assert.Contains("FILTER(Venda, Venda[Ano] = 2024)", dax);
        Assert.Contains("\"Total\", SUM(Venda[Valor])", dax);
        Assert.Contains("\"Qtd\", COUNTROWS(Venda)", dax);
        Assert.DoesNotContain("Venda[Categoria]", dax); // sem coluna de agrupamento
    }

    [Fact]
    public void GroupBy_WithoutDaxColumnOnAggregate_UsesThePropertyName()
    {
        // This used to throw: the attribute was mandatory on every aggregation property. The
        // refusal test became a behaviour test once the convention took over.
        DaxGroupedQuery<Venda, string> query = Table().GroupBy(v => v.Categoria);

        string dax = query
            .Select(g => new SemAtributo { Categoria = g.Key, Total = g.Sum(x => x.Valor) })
            .ToDaxString();

        Assert.Contains("\"Total\", SUM(Venda[Valor])", dax);
        Assert.Contains("Venda[Categoria]", dax);
    }

    private sealed class SemAtributo
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        public double? Total { get; set; } // sem [DaxColumn] -> o nome da propriedade é usado
    }
}
