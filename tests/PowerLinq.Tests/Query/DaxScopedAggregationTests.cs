using System.Linq.Expressions;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Specific scopes for measures and aggregations within the same grouped projection.
/// </summary>
public sealed class DaxScopedAggregationTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("Venda[Ano]")] public int Ano { get; set; }
        [DaxColumn("Venda[Ativo]")] public bool Ativo { get; set; }
        [DaxColumn("Venda[Valor]")] public decimal Valor { get; set; }

        [DaxColumn("Venda[TIPO_ID]")]
        [DaxNavigation("Venda[TIPO_ID]")]
        public TipoItem Tipo { get; set; } = new();
    }

    [DaxTable("DIM_TIPO")]
    private sealed class TipoItem
    {
        [DaxColumn("DIM_TIPO[CODIGO]")] public string Codigo { get; set; } = "";
    }

    private sealed class PorCategoria
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        public decimal Total { get; set; }
        public decimal Filtrado { get; set; }
        public decimal SomaCondicional { get; set; }
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

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    [Fact]
    public void MeasureWhere_RestrictsOnlyItsProjectionColumn()
    {
        string dax = Flat(Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Total = g.Measure<decimal>("Total Vendas"),
                Filtrado = g.MeasureWhere<decimal>("Total Vendas", v => v.Ano == 2024)
            })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(Venda[Categoria], "
            + "\"Total\", [Total Vendas], "
            + "\"Filtrado\", CALCULATE([Total Vendas], FILTER(Venda, Venda[Ano] = 2024)))",
            dax);
    }

    [Fact]
    public void SumWhere_RestrictsOnlyItsProjectionColumn()
    {
        string dax = Flat(Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Total = g.Sum(v => v.Valor),
                Filtrado = g.SumWhere(v => v.Valor, v => v.Ano == 2024)
            })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(Venda[Categoria], "
            + "\"Total\", SUM(Venda[Valor]), "
            + "\"Filtrado\", CALCULATE(SUM(Venda[Valor]), FILTER(Venda, Venda[Ano] = 2024)))",
            dax);
    }

    [Fact]
    public void ScopedMeasure_UsesTheSameForeignTableFilterFormAsWhere()
    {
        string[] tipos = ["00", "01"];

        string dax = Flat(Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Filtrado = g.MeasureWhere<decimal>(
                    "Total Vendas",
                    v => tipos.Contains(v.Tipo.Codigo))
            })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(Venda[Categoria], "
            + "\"Filtrado\", CALCULATE([Total Vendas], "
            + "FILTER(DIM_TIPO, DIM_TIPO[CODIGO] IN { \"00\", \"01\" })))",
            dax);
    }

    [Fact]
    public void Sum_ConditionalSelector_BecomesIfInsideSumx()
    {
        string dax = Flat(Table()
            .Aggregate(g => new Totais
            {
                Soma = g.Sum(v => v.Ativo ? v.Valor : 0)
            })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(\"Soma\", "
            + "SUMX(Venda, IF(Venda[Ativo], Venda[Valor], 0)))",
            dax);
    }

    [Fact]
    public void ConditionalAggregateExpression_BecomesIf()
    {
        bool incluir = true;

        string dax = Flat(Table()
            .Aggregate(g => new Totais
            {
                Soma = incluir ? g.Sum(v => v.Valor) : 0
            })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(\"Soma\", IF(TRUE, SUM(Venda[Valor]), 0))",
            dax);
    }

    // ---------- predicates coming from a variable, not from the array literal ----------

    [Fact]
    public void MeasureWhere_AcceptsASinglePredicateHeldInAVariable()
    {
        Expression<Func<Venda, bool>> doAno = v => v.Ano == 2024;

        string dax = Flat(Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Filtrado = g.MeasureWhere<decimal>("Total Vendas", doAno)
            })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(Venda[Categoria], "
            + "\"Filtrado\", CALCULATE([Total Vendas], FILTER(Venda, Venda[Ano] = 2024)))",
            dax);
    }

    [Fact]
    public void MeasureWhere_AcceptsAPredicateArrayHeldInAVariable()
    {
        Expression<Func<Venda, bool>>[] filtros =
        [
            v => v.Ano == 2024,
            v => v.Ativo
        ];

        string dax = Flat(Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Filtrado = g.MeasureWhere<decimal>("Total Vendas", filtros)
            })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(Venda[Categoria], "
            + "\"Filtrado\", CALCULATE([Total Vendas], "
            + "FILTER(Venda, Venda[Ano] = 2024 && Venda[Ativo])))",
            dax);
    }

    [Fact]
    public void MeasureWhere_WithANullPredicateElement_IsRefused()
    {
        var comNulo = new Expression<Func<Venda, bool>>[] { null! };

        Assert.ThrowsAny<NotSupportedException>(() => Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Filtrado = g.MeasureWhere<decimal>("Total Vendas", comNulo)
            })
            .ToDaxString());
    }

    // ---------- unsupported aggregation expression ----------

    [Fact]
    public void AnUnsupportedAggregateExpression_IsRefusedNamingTheNodeType()
    {
        NotSupportedException ex = Assert.ThrowsAny<NotSupportedException>(() => Table()
            .Aggregate(g => new Totais
            {
                Soma = -g.Sum(v => v.Valor)
            })
            .ToDaxString());

        Assert.Contains("Negate", ex.Message, StringComparison.Ordinal);
    }

    private sealed class Totais
    {
        public decimal Soma { get; set; }
    }
}
