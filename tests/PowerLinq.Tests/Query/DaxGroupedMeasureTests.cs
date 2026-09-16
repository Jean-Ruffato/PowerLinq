using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// A model measure as an <b>extension column</b> of a <c>SUMMARIZECOLUMNS</c>.
/// </summary>
/// <remarks>
/// <para>
/// It is where hand-written DAX puts the measure: <c>SUMMARIZECOLUMNS(Venda[Categoria], "Total",
/// [Total Vendas])</c>.
/// </para>
/// <para>
/// <b>The shape is a method on the group, not an attribute on the contract.</b> The initial design
/// had the attribute, but a grouping's result comes from a lambda — and if the attribute won,
/// whatever the lambda wrote into that property would be ignored <b>silently</b>. Here nothing is
/// ignored, and the measure coexists with column aggregates in the same projection.
/// </para>
/// </remarks>
public sealed class DaxGroupedMeasureTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("Venda[Valor]")] public decimal Valor { get; set; }
        [DaxColumn("Venda[Ativo]")] public bool Ativo { get; set; }
    }

    private sealed class PorCategoria
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("[Total]")] public decimal Total { get; set; }
        [DaxColumn("[Soma]")] public decimal Soma { get; set; }
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
    public void AMeasure_BecomesAnExtensionColumn()
    {
        string dax = Flat(Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Total = g.Measure<decimal>("Total Vendas") })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(Venda[Categoria], \"Total\", [Total Vendas])", dax);
    }

    /// <summary>
    /// <b>With no iterator around it.</b> The measure already is the aggregation; a <c>SUMX</c>
    /// around it would add it up once per row of the group — valid DAX, wrong number. It is the
    /// distinction phase 1 already pinned down on the scalar side, and it holds identically here.
    /// </summary>
    [Fact]
    public void TheMeasure_IsNeverWrappedInAnIterator()
    {
        string dax = Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Total = g.Measure<decimal>("Total Vendas") })
            .ToDaxString();

        Assert.DoesNotContain("SUMX", dax, StringComparison.Ordinal);
        Assert.DoesNotContain("SUM(", dax, StringComparison.Ordinal);
    }

    /// <summary>A measure and a column aggregate in the same projection — neither gets in the other's way.</summary>
    [Fact]
    public void AMeasureAndAColumnAggregate_CoexistInTheSameProjection()
    {
        string dax = Flat(Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Total = g.Measure<decimal>("Total Vendas"),
                Soma = g.Sum(v => v.Valor)
            })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(Venda[Categoria], "
            + "\"Total\", [Total Vendas], \"Soma\", SUM(Venda[Valor]))",
            dax);
    }

    [Fact]
    public void TheFiltersStillBecomeFilterTables()
    {
        string dax = Flat(Table()
            .Where(v => v.Ativo)
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Total = g.Measure<decimal>("Total Vendas") })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(Venda[Categoria], FILTER(Venda, Venda[Ativo]), "
            + "\"Total\", [Total Vendas])",
            dax);
    }

    /// <summary>
    /// The name resolves through the <b>same</b> point <c>MeasureAsync</c> uses, with or without
    /// brackets. It is the mandatory mitigation: two ways to reference a measure, a single
    /// qualification rule.
    /// </summary>
    [Theory]
    [InlineData("Total Vendas")]
    [InlineData("[Total Vendas]")]
    public void TheNameResolvesLikeTheScalarPath(string escrito)
    {
        string dax = Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Total = g.Measure<decimal>(escrito) })
            .ToDaxString();

        Assert.Contains("\"Total\", [Total Vendas]", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name that depends on the group is refused: it would have to be resolved per row, and DAX
    /// has no such form.
    /// </summary>
    [Fact]
    public void ANameComputedFromTheGroup_IsRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table()
                .GroupBy(v => v.Categoria)
                .Select(g => new PorCategoria { Total = g.Measure<decimal>(g.Key + " Total") })
                .ToDaxString());

        Assert.Contains("literal", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A captured variable counts: it is known at composition time.</summary>
    [Fact]
    public void ACapturedVariable_IsAcceptedAsTheName()
    {
        string medida = "Total Vendas";

        string dax = Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Total = g.Measure<decimal>(medida) })
            .ToDaxString();

        Assert.Contains("\"Total\", [Total Vendas]", dax, StringComparison.Ordinal);
    }
}
