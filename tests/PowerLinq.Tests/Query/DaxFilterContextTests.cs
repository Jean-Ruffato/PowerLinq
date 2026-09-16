using System.Linq.Expressions;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// <c>CALCULATE</c> and the filter-context modifiers.
/// </summary>
/// <remarks>
/// <para>
/// The central risk was named before any code: the modifiers are <b>not ordinary functions</b>.
/// Inside <c>CALCULATE</c>, <c>REMOVEFILTERS(Venda[Uf])</c> <b>removes</b> a filter and
/// <c>Venda[Uf] = "SP"</c> <b>applies</b> one — the same syntactic position, the inverse effect.
/// An API that treated them as predicates would generate DAX that says the opposite of what the author meant.
/// </para>
/// <para>
/// The answer is the API's <b>shape</b>: the parameter is a column selector, and the method's name
/// says what it does with the filter — <c>MeasureIgnoring</c> removes, <c>MeasureKeepingOnly</c>
/// keeps only what was named. A predicate is refused while naming the difference, because
/// <c>v =&gt; v.Uf == "SP"</c> is a valid selector for the compiler too.
/// </para>
/// </remarks>
public sealed class DaxFilterContextTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("Venda[Regiao]")] public string Regiao { get; set; } = "";
        [DaxColumn("Venda[Uf]")] public string Uf { get; set; } = "";
        [DaxColumn("Venda[Valor]")] public decimal Valor { get; set; }
    }

    private sealed class PorCategoria
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("[Total]")] public decimal? Total { get; set; }
        [DaxColumn("[Participacao]")] public decimal? Participacao { get; set; }
    }

    private sealed class CapturingExecutor : IDaxQueryExecutor
    {
        public string? LastQuery { get; private set; }

        public Task<List<TRow>> ExecuteAsync<TRow>(string daxQuery, CancellationToken cancellationToken = default)
            where TRow : class
        {
            LastQuery = daxQuery;
            return Task.FromResult(new List<TRow>());
        }

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            return Task.FromResult<object?>(null);
        }

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            return Task.FromResult(0);
        }
    }

    private static DaxTable<Venda> Table(CapturingExecutor executor) => new(executor);

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    // ---------- the motivating case, end to end ----------

    /// <summary>
    /// <b>Share of total, in a single query.</b> It is the motivating case, and the acceptance
    /// criterion that demands end to end.
    /// </summary>
    /// <remarks>
    /// Each row of the <c>SUMMARIZECOLUMNS</c> is evaluated with its own category's filter;
    /// removing that filter gives the total across all of them. Summing on the client is not
    /// equivalent — and would not even be possible with pagination, where the client only sees the page.
    /// </remarks>
    [Fact]
    public void ShareOfTotal_IsOneQuery()
    {
        var executor = new CapturingExecutor();

        string dax = Flat(Table(executor)
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Categoria = g.Key,
                Total = g.Measure<decimal>("Total Vendas"),
                Participacao = g.Measure<decimal>("Total Vendas")
                               / g.MeasureIgnoring<decimal>("Total Vendas", v => v.Categoria)
            })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(Venda[Categoria], "
            + "\"Total\", [Total Vendas], "
            + "\"Participacao\", [Total Vendas] "
            + "/ CALCULATE([Total Vendas], REMOVEFILTERS(Venda[Categoria])))",
            dax);
    }

    // ---------- the modifiers ----------

    [Fact]
    public void MeasureIgnoringAColumn_EmitsRemoveFiltersOnThatColumn()
    {
        string dax = Flat(Table(new CapturingExecutor())
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Categoria = g.Key,
                Total = g.MeasureIgnoring<decimal>("Total Vendas", v => v.Uf)
            })
            .ToDaxString());

        Assert.Contains(
            "CALCULATE([Total Vendas], REMOVEFILTERS(Venda[Uf]))", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no columns at all, the target is the <b>table</b> — and not <c>REMOVEFILTERS()</c>,
    /// which would remove the filter from everything, including tables the query never mentions.
    /// Far too powerful to come out of an omitted argument.
    /// </summary>
    [Fact]
    public void MeasureIgnoringNothing_TargetsTheTableAndNotEverything()
    {
        string dax = Flat(Table(new CapturingExecutor())
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Categoria = g.Key,
                Total = g.MeasureIgnoring<decimal>("Total Vendas")
            })
            .ToDaxString());

        Assert.Contains(
            "CALCULATE([Total Vendas], REMOVEFILTERS(Venda))", dax, StringComparison.Ordinal);
        Assert.DoesNotContain("REMOVEFILTERS()", dax, StringComparison.Ordinal);
    }

    /// <summary>More than one column goes into the same <c>REMOVEFILTERS</c>, in declared order.</summary>
    [Fact]
    public void SeveralColumns_GoIntoOneRemoveFilters()
    {
        string dax = Flat(Table(new CapturingExecutor())
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Categoria = g.Key,
                Total = g.MeasureIgnoring<decimal>("Total Vendas", v => v.Uf, v => v.Regiao)
            })
            .ToDaxString());

        Assert.Contains(
            "REMOVEFILTERS(Venda[Uf], Venda[Regiao])", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>MeasureKeepingOnly</c> becomes <c>ALLEXCEPT</c>, and the list is of what <b>survives</b>
    /// — the opposite of what a naive reading of the DAX name suggests. It is the partial subtotal:
    /// grouped by region and category, keeping only the region gives the region's total.
    /// </summary>
    [Fact]
    public void MeasureKeepingOnly_EmitsAllExceptWithWhatSurvives()
    {
        string dax = Flat(Table(new CapturingExecutor())
            .GroupBy(v => new { v.Regiao, v.Categoria })
            .Select(g => new PorCategoria
            {
                Total = g.MeasureKeepingOnly<decimal>("Total Vendas", v => v.Regiao)
            })
            .ToDaxString());

        Assert.Contains(
            "CALCULATE([Total Vendas], ALLEXCEPT(Venda, Venda[Regiao]))", dax, StringComparison.Ordinal);
    }

    // ---------- the scalar measure ----------

    /// <summary>
    /// In the scalar measure the composed <c>Where</c> goes in as filter context, and the modifier
    /// <b>undoes</b> the part of it that talks about the given column — both in the same
    /// <c>CALCULATE</c>, which is what gives the ratio over the total.
    /// </summary>
    [Fact]
    public async Task OnAScalarMeasure_TheComposedFilterAndTheModifierCoexist()
    {
        var executor = new CapturingExecutor();

        await Table(executor)
            .Where(v => v.Uf == "SP")
            .MeasureIgnoringAsync<decimal>("Total Vendas", [v => v.Uf]);

        Assert.Equal(
            "EVALUATE ROW(\"[Value]\", CALCULATE([Total Vendas], "
            + "FILTER(Venda, Venda[Uf] = \"SP\"), REMOVEFILTERS(Venda[Uf])))",
            Flat(executor.LastQuery!));
    }

    /// <summary>
    /// The scalar measure has the grouped one's same pair: <c>MeasureKeepingOnlyAsync</c> emits
    /// <c>ALLEXCEPT</c>, and the list is of what <b>survives</b>.
    /// </summary>
    [Fact]
    public async Task OnAScalarMeasure_KeepingOnly_EmitsAllExcept()
    {
        var executor = new CapturingExecutor();

        await Table(executor).MeasureKeepingOnlyAsync<decimal>("Total Vendas", [v => v.Regiao]);

        Assert.Equal(
            "EVALUATE ROW(\"[Value]\", CALCULATE([Total Vendas], ALLEXCEPT(Venda, Venda[Regiao])))",
            Flat(executor.LastQuery!));
    }

    [Fact]
    public async Task OnAScalarMeasure_KeepingOnlyNothing_IsRefused()
    {
        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => Table(new CapturingExecutor()).MeasureKeepingOnlyAsync<decimal>("Total Vendas", []));

        Assert.Contains("at least one column", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnAScalarMeasure_KeepingOnly_RefusesANullColumnArray() =>
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Table(new CapturingExecutor()).MeasureKeepingOnlyAsync<decimal>("Total Vendas", null!));

    /// <summary>With no filter and no modifier there is no <c>CALCULATE</c> — the DAX from before.</summary>
    [Fact]
    public async Task WithoutFilterOrModifier_ThereIsNoCalculate()
    {
        var executor = new CapturingExecutor();

        await Table(executor).Where(v => v.Uf == "SP").MeasureAsync<decimal>("Total Vendas");

        Assert.DoesNotContain("REMOVEFILTERS", executor.LastQuery!, StringComparison.Ordinal);
        Assert.Contains("CALCULATE(", executor.LastQuery!, StringComparison.Ordinal);
    }

    // ---------- what is refused ----------

    /// <summary>
    /// <b>It is the required refusal.</b> A predicate in place of the selector is refused while
    /// naming the difference between removing and applying a filter — translating it would produce DAX that says the opposite.
    /// </summary>
    [Fact]
    public void APredicateInsteadOfAColumn_IsRefusedNamingTheDifference()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table(new CapturingExecutor())
                .GroupBy(v => v.Categoria)
                .Select(g => new PorCategoria
                {
                    Categoria = g.Key,
                    Total = g.MeasureIgnoring<decimal>("Total Vendas", v => v.Uf == "SP")
                })
                .ToDaxString());

        Assert.Contains("takes a column, not a predicate", ex.Message, StringComparison.Ordinal);
        Assert.Contains("remove filters", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Where", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>And on the scalar measure too, through the same translation path.</summary>
    [Fact]
    public async Task APredicateOnTheScalarMeasure_IsRefusedToo()
    {
        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => Table(new CapturingExecutor())
                .MeasureIgnoringAsync<decimal>("Total Vendas", [v => v.Uf == "SP"]));

        Assert.Contains("takes a column, not a predicate", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>MeasureKeepingOnly</c> with no column is refused: it would equal ignoring the whole
    /// table, and a one-argument <c>ALLEXCEPT</c> only makes whoever reads the DAX wonder what was excepted.
    /// </summary>
    [Fact]
    public void KeepingOnlyNothing_IsRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table(new CapturingExecutor())
                .GroupBy(v => v.Categoria)
                .Select(g => new PorCategoria
                {
                    Categoria = g.Key,
                    Total = g.MeasureKeepingOnly<decimal>("Total Vendas")
                })
                .ToDaxString());

        Assert.Contains("at least one column", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A list of selectors kept in a variable does not arrive as a <c>NewArrayExpression</c> — the
    /// compiler closes it into a constant — and the refusal describes what came in instead of
    /// blowing up in a cast. The form that works is writing the selectors in the call itself.
    /// </summary>
    [Fact]
    public void ASelectorListHeldInAVariable_IsRefusedDescribingWhatArrived()
    {
        Expression<Func<Venda, object?>>[] colunas = [v => v.Uf];

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table(new CapturingExecutor())
                .GroupBy(v => v.Categoria)
                .Select(g => new PorCategoria
                {
                    Categoria = g.Key,
                    Total = g.MeasureIgnoring<decimal>("Total Vendas", colunas)
                })
                .ToDaxString());

        Assert.Contains("takes a column, not", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal is localized, like the rest: the message is the piece that carries the decision,
    /// and one that existed only in English would leave half the users without it.
    /// </summary>
    [Fact]
    public void TheRefusal_IsLocalized()
    {
        IDaxTable<Venda> table = new DaxTableFactory(
            new PowerLinq.DaxConverter.Localization.ResourceManagerPowerLinqLocalizer("pt-BR"))
            .Create<Venda>(new CapturingExecutor());

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => table
                .GroupBy(v => v.Categoria)
                .Select(g => new PorCategoria
                {
                    Categoria = g.Key,
                    Total = g.MeasureIgnoring<decimal>("Total Vendas", v => v.Uf == "SP")
                })
                .ToDaxString());

        Assert.Contains("recebe uma coluna, não a predicate", ex.Message, StringComparison.Ordinal);
    }
}
