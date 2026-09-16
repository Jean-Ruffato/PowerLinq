using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Ordering declared before the grouping.
/// </summary>
/// <remarks>
/// <para>
/// <c>BuildGroupBy</c> never read <c>definition.OrderBy</c>, so the ordering <b>disappeared
/// silently</b>: out came a <c>SUMMARIZECOLUMNS</c> with no <c>ORDER BY</c> and no error. The
/// symptom showed up as "the grid came out unsorted", once it was already in production.
/// </para>
/// <para>
/// Worse than the discarding was the inconsistency: the <c>Skip</c>/<c>Take</c> guard raises an
/// explicit error and its message <b>announced <c>OrderBy</c> as supported</b> in that position.
/// Anyone who read the message had every reason to believe they had sorted.
/// </para>
/// </remarks>
public sealed class DaxGroupedOrderByTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("Venda[Regiao]")] public string Regiao { get; set; } = "";
        [DaxColumn("Venda[Valor]")] public decimal Valor { get; set; }
    }

    private sealed class PorCategoria
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        public decimal Total { get; set; }
    }

    private sealed class PorCategoriaERegiao
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("Venda[Regiao]")] public string Regiao { get; set; } = "";
        public decimal Total { get; set; }
    }

    private sealed class Totais
    {
        public decimal Total { get; set; }
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

    // ---------- ordering by a key column: honoured ----------

    [Fact]
    public void OrderByAGroupingKey_ReachesTheGeneratedDax()
    {
        string dax = Table()
            .OrderBy(v => v.Categoria)
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
            .ToDaxString();

        Assert.Contains("SUMMARIZECOLUMNS(", dax);
        Assert.Contains("ORDER BY Venda[Categoria] ASC", dax);
    }

    [Fact]
    public void OrderByDescendingAGroupingKey_KeepsTheDirection()
    {
        string dax = Table()
            .OrderByDescending(v => v.Categoria)
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
            .ToDaxString();

        Assert.Contains("ORDER BY Venda[Categoria] DESC", dax);
    }

    /// <summary>
    /// A composite key. The selector assigns only the aggregation: the key columns enter the
    /// <c>SUMMARIZECOLUMNS</c> through the <c>GroupBy</c> and reach the DTO through the column mapping.
    /// Assigning them through <c>g.Key.Categoria</c> is not supported yet — <c>IsKeyAccess</c> only
    /// recognizes <c>g.Key</c> as a whole.
    /// </summary>
    [Fact]
    public void OrderByTwoGroupingKeys_KeepsBothTermsInOrder()
    {
        string dax = Table()
            .OrderBy(v => v.Categoria)
            .ThenByDescending(v => v.Regiao)
            .GroupBy(v => new { v.Categoria, v.Regiao })
            .Select(g => new PorCategoriaERegiao { Total = g.Sum(v => v.Valor) })
            .ToDaxString();

        Assert.Contains("Venda[Categoria]", dax);
        Assert.Contains("Venda[Regiao]", dax);
        Assert.Contains("ORDER BY Venda[Categoria] ASC, Venda[Regiao] DESC", dax);
    }

    [Fact]
    public void WithoutOrderBy_TheGroupedQueryIsUnchanged()
    {
        string dax = Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
            .ToDaxString();

        Assert.DoesNotContain("ORDER BY", dax);
    }

    // ---------- ordering by a non-key column: refused ----------

    /// <summary>
    /// Refused for semantic impossibility, not as a limitation: the aggregation collapses the
    /// group's rows, so there is no per-output-row <c>Valor</c> left to sort by.
    /// </summary>
    [Fact]
    public void OrderByANonKeyColumn_IsRefusedNamingTheColumnAndTheKeys()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => Table()
            .OrderByDescending(v => v.Valor)
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
            .ToDaxString());

        Assert.Contains("Venda[Valor]", ex.Message);
        Assert.Contains("Venda[Categoria]", ex.Message);
        Assert.Contains("not a grouping key", ex.Message);
    }

    [Fact]
    public void OrderByBeforeAggregate_IsRefusedBecauseThereIsNoKeyAtAll()
    {
        // Aggregate returns a single row; ordering means nothing, and it used to be discarded
        // along with everything else.
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => Table()
            .OrderBy(v => v.Categoria)
            .Aggregate(g => new Totais { Total = g.Sum(v => v.Valor) })
            .ToDaxString());

        Assert.Contains("Venda[Categoria]", ex.Message);
    }

    [Fact]
    public void OneKeyOrderedAndOneNotKey_IsStillRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => Table()
            .OrderBy(v => v.Categoria)
            .ThenBy(v => v.Valor)
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
            .ToDaxString());

        Assert.Contains("Venda[Valor]", ex.Message);
    }

    [Fact]
    public void TheRefusalMessage_IsLocalized()
    {
        IPowerLinqLocalizer portuguese = new ResourceManagerPowerLinqLocalizer("pt-BR");

        Assert.Contains(
            "não é chave do agrupamento",
            portuguese.Format("OrderByColumnNotGrouped", "Venda[Valor]", "Venda[Categoria]"));
    }

    // ---------- the guard's message does not promise more than it delivers ----------

    /// <summary>
    /// The <c>Skip</c>/<c>Take</c> message announced "the supported order is Where, OrderBy,
    /// Skip, Take", which made the caller believe that <c>OrderBy</c> before <c>GroupBy</c> always
    /// held. It now holds over a key, and the message says exactly that.
    /// </summary>
    [Fact]
    public void TheWindowGuardMessage_QualifiesWhatOrderByDoesBeforeGrouping()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => Table()
            .Take(5)
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) }));

        Assert.Contains("only over a grouping key", ex.Message);
    }
}
