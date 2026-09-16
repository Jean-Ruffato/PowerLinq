using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;
using Syntax = PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Server-side deduplication — <c>Distinct()</c> as a stage, and <c>DistinctValuesAsync</c> as the
/// shortcut for a single column.
/// </summary>
/// <remarks>
/// <para>
/// The shortcut exists for a concrete reason: a filter's option list required declaring a type
/// with a single property, whose only reason to exist was that there was no way to project a
/// column. A real repository accumulates several of those.
/// </para>
/// <para>
/// Choosing <c>DISTINCT</c> over a table expression, rather than <c>VALUES</c> over a column, is
/// deliberate: <c>VALUES</c> adds the blank row when there is a referential integrity violation in
/// the model, which would give an option list an unexplained empty item.
/// </para>
/// </remarks>
public sealed class DaxDistinctTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[Exportador]")] public string Exportador { get; set; } = "";
        [DaxColumn("Venda[Periodo]")] public string Periodo { get; set; } = "";
        [DaxColumn("Venda[Valor]")] public decimal Valor { get; set; }
        [DaxColumn("Venda[Ativo]")] public bool Ativo { get; set; }
    }

    private sealed class Projetado
    {
        public string Exportador { get; set; } = "";
    }

    /// <summary>Returns one column named <c>[Value]</c> carrying the given values.</summary>
    private sealed class ValuesExecutor(params string[] values) : IDaxQueryExecutor
    {
        public string? LastQuery { get; private set; }

        public Task<List<TRow>> ExecuteAsync<TRow>(string daxQuery, CancellationToken cancellationToken = default)
            where TRow : class
        {
            LastQuery = daxQuery;

            var rows = new List<TRow>();

            foreach (string value in values)
            {
                var row = Activator.CreateInstance<TRow>();
                // The internal contract's Value property; with no [DaxColumn], the mapping registers
                // it under Value and [Value] — here the test fills it by reflection, as the mapper would.
                row.GetType().GetProperty("Value")?.SetValue(row, value);
                rows.Add(row);
            }

            return Task.FromResult(rows);
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

    private static DaxTable<Venda> Table(IDaxQueryExecutor? executor = null) =>
        new(executor ?? new ValuesExecutor());

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    // ---------- Distinct as a stage ----------

    [Fact]
    public void Distinct_WrapsTheSource()
    {
        Assert.Equal("EVALUATE DISTINCT(Venda)", Flat(Table().Distinct().ToDaxString()));
    }

    [Fact]
    public void Distinct_AfterWhere_DeduplicatesTheFilteredSource()
    {
        string dax = Flat(Table().Where(v => v.Ativo).Distinct().ToDaxString());

        Assert.Equal("EVALUATE DISTINCT(FILTER(Venda, Venda[Ativo]))", dax);
    }

    /// <summary>The natural use: reduce the columns and then deduplicate.</summary>
    [Fact]
    public void Distinct_AfterProjection_DeduplicatesTheProjectedColumns()
    {
        string dax = Flat(Table()
            .Select(v => new Projetado { Exportador = v.Exportador })
            .Distinct()
            .ToDaxString());

        Assert.Equal(
            "EVALUATE DISTINCT(SELECTCOLUMNS(Venda, \"Exportador\", Venda[Exportador]))",
            dax);
    }

    /// <summary>
    /// <c>DISTINCT</c> changes the rows, not the columns — so the column resolution of the
    /// following operators still holds over the projection.
    /// </summary>
    [Fact]
    public void Distinct_DoesNotChangeTheResultColumns()
    {
        string dax = Flat(Table()
            .Select(v => new Projetado { Exportador = v.Exportador })
            .Distinct()
            .Where(r => r.Exportador != "")
            .ToDaxString());

        Assert.Equal(
            "EVALUATE FILTER(DISTINCT(SELECTCOLUMNS(Venda, \"Exportador\", Venda[Exportador])), "
                + "[Exportador] <> \"\")",
            dax);

        // The distinction that matters: the predicate uses the RESULT's column. If DISTINCT cleared
        // the column set, resolution would fall back to the mapping and emit Venda[Exportador].
        Assert.DoesNotContain("), Venda[Exportador] <>", dax, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CountAsync_AfterDistinct_CountsTheDistinctRows()
    {
        var executor = new ValuesExecutor();

        await Table(executor)
            .Select(v => new Projetado { Exportador = v.Exportador })
            .Distinct()
            .CountAsync();

        Assert.StartsWith(
            "EVALUATE ROW(\"[Count]\", COUNTROWS(DISTINCT(SELECTCOLUMNS(",
            Flat(executor.LastQuery!));
    }

    /// <summary>
    /// The builder's ordering check still holds <b>after</b> the <c>DISTINCT</c>.
    /// </summary>
    /// <remarks>
    /// The fold carries the column set on its own, separate from what
    /// <c>DaxPipeline.ResultColumns</c> computes for composition. If the deduplication stage
    /// cleared that accumulator, the builder would stop refusing an invalid ordering — and no
    /// composition test would notice, because composition uses the other path. This test is what
    /// closes that gap.
    /// </remarks>
    [Fact]
    public void TheBuilder_StillChecksTheOrderAfterADistinct()
    {
        var pipeline = new DaxPipeline("Venda", typeof(object))
        {
            Stages =
            [
                new DaxProjectStage(
                    [new DaxProjectionColumn("Exportador", new Syntax.DaxColumnRef("Venda[Exportador]"))]),
                new DaxDistinctStage(),
                new DaxOrderStage(
                    new Syntax.DaxOrderTerm(new Syntax.DaxColumnRef("Venda[Valor]"), false),
                    ResetsOrder: true)
            ]
        };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => DaxConverter.Builders.DaxPipelineBuilder.Build(pipeline));

        Assert.Contains("Venda[Valor]", ex.Message);
        Assert.Contains("[Exportador]", ex.Message);
    }

    // ---------- DistinctValuesAsync ----------

    [Fact]
    public async Task DistinctValuesAsync_ProjectsOneColumnAndDeduplicates()
    {
        var executor = new ValuesExecutor("ACME", "GLOBEX");

        List<string> valores = await Table(executor).DistinctValuesAsync(v => v.Exportador);

        Assert.Equal(["ACME", "GLOBEX"], valores);
        Assert.Equal(
            "EVALUATE DISTINCT(SELECTCOLUMNS(Venda, \"Value\", Venda[Exportador]))",
            Flat(executor.LastQuery!));
    }

    [Fact]
    public async Task DistinctValuesAsync_AfterWhere_KeepsTheFilter()
    {
        var executor = new ValuesExecutor("ACME");

        await Table(executor).Where(v => v.Ativo).DistinctValuesAsync(v => v.Exportador);

        Assert.Equal(
            "EVALUATE DISTINCT(SELECTCOLUMNS(FILTER(Venda, Venda[Ativo]), \"Value\", Venda[Exportador]))",
            Flat(executor.LastQuery!));
    }

    /// <summary>
    /// The earlier ordering is <b>rewritten</b> onto the projected column, so the list comes back
    /// sorted by the server — which is what a filter's option list needs.
    /// </summary>
    [Fact]
    public async Task DistinctValuesAsync_AfterOrderBy_ComesOrderedFromTheServer()
    {
        var executor = new ValuesExecutor("ACME", "GLOBEX");

        await Table(executor).OrderBy(v => v.Exportador).DistinctValuesAsync(v => v.Exportador);

        Assert.EndsWith("ORDER BY [Value] ASC", Flat(executor.LastQuery!));
    }

    /// <summary>
    /// Ordering by one column and asking for <b>another</b> one's values is refused: a
    /// single-column projection does not carry the ordering one forward, and the <c>ORDER BY</c>
    /// only references what the result has.
    /// </summary>
    [Fact]
    public async Task DistinctValuesAsync_OrderedByAnotherColumn_IsRefused()
    {
        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => Table().OrderBy(v => v.Periodo).DistinctValuesAsync(v => v.Exportador));

        Assert.Contains("Venda[Periodo]", ex.Message);
    }

    [Fact]
    public async Task DistinctValuesAsync_OfANumericColumn_MaterializesTheValueType()
    {
        // The internal contract is generic, so the column's type reaches the caller with no manual
        // conversion — that is what makes the DTO unnecessary.
        var executor = new ValuesExecutor();

        List<decimal> valores = await Table(executor).DistinctValuesAsync(v => v.Valor);

        Assert.Empty(valores);
        Assert.Contains("\"Value\", Venda[Valor]", Flat(executor.LastQuery!), StringComparison.Ordinal);
    }

    // ---------- ValuesAsync: the scalar projection ----------

    /// <summary>
    /// The scalar projection. <c>Select</c> does not serve: it returns a composable query, and that
    /// query's result type has to be materializable — <c>string</c> is not a contract.
    /// </summary>
    [Fact]
    public async Task ValuesAsync_ProjectsOneColumnWithoutDeduplicating()
    {
        var executor = new ValuesExecutor("ACME", "ACME", "GLOBEX");

        List<string> valores = await Table(executor).ValuesAsync(v => v.Exportador);

        // The duplicates survive — that is the difference from DistinctValuesAsync.
        Assert.Equal(["ACME", "ACME", "GLOBEX"], valores);
        Assert.Equal(
            "EVALUATE SELECTCOLUMNS(Venda, \"Value\", Venda[Exportador])",
            Flat(executor.LastQuery!));
        Assert.DoesNotContain("DISTINCT(", executor.LastQuery!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValuesAsync_AfterWhereAndOrderBy_KeepsBoth()
    {
        var executor = new ValuesExecutor("ACME");

        await Table(executor)
            .Where(v => v.Ativo)
            .OrderByDescending(v => v.Exportador)
            .ValuesAsync(v => v.Exportador);

        Assert.Equal(
            "EVALUATE SELECTCOLUMNS(FILTER(Venda, Venda[Ativo]), \"Value\", Venda[Exportador]) "
                + "ORDER BY [Value] DESC",
            Flat(executor.LastQuery!));
    }

    [Fact]
    public async Task ValuesAsync_OfAComputedExpression_ProjectsTheExpression()
    {
        var executor = new ValuesExecutor();

        List<decimal> valores = await Table(executor).ValuesAsync(v => v.Valor * 2m);

        Assert.Empty(valores);
        Assert.Contains("\"Value\", Venda[Valor] * 2", Flat(executor.LastQuery!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValuesAsync_FromTheTable_ForwardsToTheQuery()
    {
        var executor = new ValuesExecutor("ACME");

        List<string> valores = await new DaxTable<Venda>(executor).ValuesAsync(v => v.Exportador);

        Assert.Equal(["ACME"], valores);
    }

    [Fact]
    public async Task DistinctValuesAsync_FromTheTable_ForwardsToTheQuery()
    {
        var executor = new ValuesExecutor("ACME");

        List<string> valores = await new DaxTable<Venda>(executor)
            .DistinctValuesAsync(v => v.Exportador);

        Assert.Equal(["ACME"], valores);
    }
}
