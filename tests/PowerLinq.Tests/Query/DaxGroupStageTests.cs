using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Grouping became a pipeline <b>stage</b>, so <c>Aggregate</c> and
/// <c>GroupBy(...).Select(...)</c> return <see cref="DaxQuery{TResult}"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the stage that <b>does not wrap the source</b>, and that is what makes it different
/// from all the others. <c>FILTER</c>, <c>TOPN</c> and <c>SELECTCOLUMNS</c> take a table and return
/// another, so folding them is nesting. <c>SUMMARIZECOLUMNS</c> takes <b>grouping columns</b> and
/// <b>filter tables</b> as sibling arguments — there is no position where the accumulated table fits.
/// That is why the fold carries the filters in a list parallel to the source.
/// </para>
/// <para>
/// A direct consequence: grouping after <c>Take</c> or <c>Skip</c> is still refused, because the
/// window has no way to become a filter table without changing the meaning. On the other hand,
/// <b>limiting</b> and <b>counting</b> the aggregated result came to work, because there the
/// <c>SUMMARIZECOLUMNS</c> is the source of whatever comes next.
/// </para>
/// </remarks>
public sealed class DaxGroupStageTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[Id]")] public int Id { get; set; }
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("Venda[Valor]")] public decimal Valor { get; set; }
        [DaxColumn("Venda[Ativo]")] public bool Ativo { get; set; }

        /// <summary>A column from another model table, reached through the relationship.</summary>
        [DaxColumn("Cliente[Nome]")] public string NomeCliente { get; set; } = "";
    }

    private sealed class PorCategoria
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("[Total]")] public decimal Total { get; set; }
    }

    private sealed class Totais
    {
        [DaxColumn("[Total]")] public decimal Total { get; set; }
    }

    private sealed class RecordingExecutor(int rows = 0) : IDaxQueryExecutor
    {
        public string? LastQuery { get; private set; }

        public Task<List<TRow>> ExecuteAsync<TRow>(string daxQuery, CancellationToken cancellationToken = default)
            where TRow : class
        {
            LastQuery = daxQuery;
            return Task.FromResult(Enumerable.Range(0, rows).Select(_ => Activator.CreateInstance<TRow>()).ToList());
        }

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            return Task.FromResult<object?>(rows);
        }

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            return Task.FromResult(rows);
        }
    }

    private static DaxTable<Venda> Table(IDaxQueryExecutor? executor = null) =>
        new(executor ?? new RecordingExecutor());

    private static IDaxTable<Venda> PortugueseTable() =>
        new DaxTableFactory(new ResourceManagerPowerLinqLocalizer("pt-BR"))
            .Create<Venda>(new RecordingExecutor());

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    // ---------- the DAX ----------

    [Fact]
    public void GroupBy_EmitsSummarizeColumns()
    {
        string dax = Flat(Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(Venda[Categoria], \"Total\", SUM(Venda[Valor]))",
            dax);
    }

    /// <summary>
    /// The filter comes in as a <b>filter table</b>, a sibling argument of the keys — not wrapping
    /// the <c>SUMMARIZECOLUMNS</c>, which is what the fold does with every other stage.
    /// </summary>
    [Fact]
    public void GroupBy_AfterWhere_PassesTheFilterAsAFilterTable()
    {
        string dax = Flat(Table()
            .Where(v => v.Ativo)
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(Venda[Categoria], FILTER(Venda, Venda[Ativo]), \"Total\", SUM(Venda[Valor]))",
            dax);
    }

    [Fact]
    public void Aggregate_WithoutKey_EmitsSummarizeColumnsWithNoGroupingColumn()
    {
        string dax = Flat(Table()
            .Where(v => v.Ativo)
            .Aggregate(g => new Totais { Total = g.Sum(v => v.Valor) })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(FILTER(Venda, Venda[Ativo]), \"Total\", SUM(Venda[Valor]))",
            dax);
    }

    // ---------- what grouping came to allow ----------

    /// <summary>
    /// Limiting the aggregated result. The <c>SUMMARIZECOLUMNS</c> is the source of the
    /// <c>TOPN</c>, which is only expressible because grouping is a stage and not a terminal.
    /// </summary>
    [Fact]
    public void Take_AfterGrouping_LimitsTheGroups()
    {
        string dax = Flat(Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
            .Take(5)
            .ToDaxString());

        Assert.StartsWith("EVALUATE TOPN(5, SUMMARIZECOLUMNS(", dax);
    }

    [Fact]
    public async Task CountAsync_AfterGrouping_CountsTheGroups()
    {
        var executor = new RecordingExecutor(rows: 3);

        int grupos = await Table(executor)
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
            .CountAsync();

        Assert.Equal(3, grupos);
        Assert.StartsWith(
            "EVALUATE ROW(\"[Count]\", COUNTROWS(SUMMARIZECOLUMNS(",
            Flat(executor.LastQuery!));
    }

    [Fact]
    public async Task ToArrayAsync_AfterGrouping_Materializes()
    {
        PorCategoria[] linhas = await Table(new RecordingExecutor(rows: 2))
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
            .ToArrayAsync();

        Assert.Equal(2, linhas.Length);
    }

    /// <summary>
    /// A keyless aggregation returns a single row, so <c>SingleAsync</c> is the natural read — and
    /// it did not exist: <c>DaxAggregateQuery</c> had <c>ToListAsync</c> and
    /// <c>FirstOrDefaultAsync</c>.
    /// </summary>
    [Fact]
    public async Task SingleAsync_AfterKeylessAggregate_ReadsTheOnlyRow()
    {
        Totais totais = await Table(new RecordingExecutor(rows: 1))
            .Aggregate(g => new Totais { Total = g.Sum(v => v.Valor) })
            .SingleAsync();

        Assert.NotNull(totais);
    }

    // ---------- what is still refused ----------

    /// <summary>
    /// The case that makes the <c>AddFilter</c> shortcut necessary: a grouping key may point at
    /// <b>another</b> model table, and then the result's column carries that table's name —
    /// <c>Cliente[Nome]</c>.
    /// </summary>
    /// <remarks>
    /// Without the shortcut, the owner-table classification would see <c>Cliente</c> as a foreign
    /// table and would produce <c>CALCULATETABLE(SUMMARIZECOLUMNS(...), FILTER(Cliente, ...))</c> —
    /// filter context over the model table, when what is wanted is to filter the <b>rows of the
    /// aggregated result</b>.
    /// </remarks>
    [Fact]
    public void Where_AfterGroupingByARelatedColumn_FiltersTheResultAndNotTheModelTable()
    {
        string dax = Flat(Table()
            .GroupBy(v => v.NomeCliente)
            .Select(g => new PorCliente { Cliente = g.Key, Total = g.Sum(v => v.Valor) })
            .Where(r => r.Cliente == "ACME")
            .ToDaxString());

        Assert.Equal(
            "EVALUATE FILTER(SUMMARIZECOLUMNS(Cliente[Nome], \"Total\", SUM(Venda[Valor])), "
                + "Cliente[Nome] = \"ACME\")",
            dax);

        Assert.DoesNotContain("CALCULATETABLE(", dax, StringComparison.Ordinal);
    }

    /// <summary>Grouping by a column from another model table.</summary>
    private sealed class PorCliente
    {
        [DaxColumn("Cliente[Nome]")] public string Cliente { get; set; } = "";
        [DaxColumn("[Total]")] public decimal Total { get; set; }
    }

    [Fact]
    public void GroupBy_AfterTake_StillThrows()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table().Take(5).GroupBy(v => v.Categoria));

        Assert.StartsWith("GroupBy cannot be applied after Take", ex.Message);
    }

    /// <summary>
    /// The same refusal exists in the builder, not only in composition: <c>DaxPipelineBuilder</c> is
    /// public, so a hand-built pipeline with a window before the grouping is refused too, instead of
    /// generating DAX that discards the window.
    /// </summary>
    [Fact]
    public void TheBuilder_RefusesAGroupStageAfterAWindow()
    {
        var pipeline = new DaxPipeline("Venda", typeof(object))
        {
            Stages =
            [
                new DaxTakeStage(5),
                new DaxGroupStage([], [])
            ]
        };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => DaxConverter.Builders.DaxPipelineBuilder.Build(pipeline));

        Assert.StartsWith("GroupBy cannot be applied after Take", ex.Message);
    }

    [Theory]
    [InlineData("SumAsync")]
    public void OperatorsThatResolveAColumn_AreRefusedAfterGrouping(string @operator)
    {
        DaxQuery<PorCategoria> agrupada = Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) });

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
        {
            switch (@operator)
            {
                default:
                    agrupada.SumAsync(r => r.Total).GetAwaiter().GetResult();
                    break;
            }
        });

        Assert.Contains("after GroupBy", ex.Message);
        Assert.Contains("CountAsync", ex.Message);
    }

    [Fact]
    public void TheRefusalAfterGrouping_IsLocalized()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => PortugueseTable()
                .Aggregate(g => new Totais { Total = g.Sum(v => v.Valor) })
                .GroupBy(r => r.Total));

        Assert.StartsWith("GroupBy não pode ser aplicado depois de GroupBy", ex.Message);
    }

    /// <summary>
    /// <c>Where</c> after aggregating filters the aggregated result — SQL's <c>HAVING</c>. The
    /// reference comes out as <c>[Total]</c>, the extension column, and not <c>Venda[Total]</c>.
    /// </summary>
    [Fact]
    public void Where_AfterGrouping_FiltersTheAggregatedResult()
    {
        string dax = Flat(Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
            .Where(r => r.Total > 1000m)
            .ToDaxString());

        Assert.Equal(
            "EVALUATE FILTER(SUMMARIZECOLUMNS(Venda[Categoria], \"Total\", SUM(Venda[Valor])), "
                + "[Total] > 1000)",
            dax);

        Assert.DoesNotContain("Venda[Total]", dax, StringComparison.Ordinal);
    }

    [Fact]
    public void Where_AfterGrouping_ByAKeyColumn_UsesTheQualifiedReference()
    {
        string dax = Flat(Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
            .Where(r => r.Categoria == "A")
            .ToDaxString());

        Assert.EndsWith("Venda[Categoria] = \"A\")", dax);
    }

    /// <summary>
    /// Ordering by a column that is not a grouping key is still refused, and not because of an
    /// implementation limit: the aggregation collapses the group's rows, so no value of that column
    /// is left per output row to order by.
    /// </summary>
    [Fact]
    public void OrderBy_BeforeGrouping_OverANonKeyColumn_StillThrows()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table()
                .OrderByDescending(v => v.Valor)
                .GroupBy(v => v.Categoria)
                .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
                .ToDaxString());

        Assert.Contains("Venda[Valor]", ex.Message);
        Assert.Contains("Venda[Categoria]", ex.Message);
    }

    [Fact]
    public void OrderBy_BeforeGrouping_OverAKeyColumn_IsHonoured()
    {
        string dax = Flat(Table()
            .OrderBy(v => v.Categoria)
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
            .ToDaxString());

        Assert.EndsWith("ORDER BY Venda[Categoria] ASC", dax);
    }
}
