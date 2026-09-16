using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Builders;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Ordering the result <b>after</b> aggregating, projecting or joining, including by an extension
/// column — the Top N by value, which is a dashboard's most common operation.
/// </summary>
/// <remarks>
/// <para>
/// The difficulty is not generating the <c>ORDER BY</c>: it is <b>resolving the column</b>. After a
/// <c>SUMMARIZECOLUMNS</c> the result carries the keys with the table reference
/// (<c>Venda[Categoria]</c>) and the extensions with the name in brackets (<c>[Total]</c>).
/// Resolving through the source entity's mapping would emit <c>Venda[Total]</c>, which does not
/// exist — so refusing was more honest, and that is what was done.
/// </para>
/// <para>
/// What is added is resolving against the <b>closed</b> set of columns the result carries. That
/// makes both shapes reachable through the same call, and turns the refusal into an error only
/// when the column really is not there — naming the ones that are.
/// </para>
/// </remarks>
public sealed class DaxOrderAggregatedTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("Venda[Regiao]")] public string Regiao { get; set; } = "";
        [DaxColumn("Venda[Valor]")] public decimal Valor { get; set; }
        [DaxColumn("Venda[Ativo]")] public bool Ativo { get; set; }
    }

    private sealed class PorCategoria
    {
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("[Total]")] public decimal Total { get; set; }
        [DaxColumn("[Pedidos]")] public long Pedidos { get; set; }
    }

    private sealed class Projetado
    {
        public string Categoria { get; set; } = "";
        public decimal Valor { get; set; }
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

    private static DaxQuery<PorCategoria> PorCategoriaQuery(IDaxQueryExecutor? executor = null) =>
        Table(executor)
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria
            {
                Categoria = g.Key,
                Total = g.Sum(v => v.Valor),
                Pedidos = g.Count()
            });

    // ---------- the use case: Top N by aggregated value ----------

    /// <summary>The acceptance criterion, and the reason the case exists.</summary>
    [Fact]
    public async Task TopNByAggregatedValue_IsASingleServerQuery()
    {
        var executor = new RecordingExecutor(rows: 10);

        await PorCategoriaQuery(executor)
            .OrderByDescending(r => r.Total)
            .Take(10)
            .ToListAsync();

        string dax = Flat(executor.LastQuery!);

        Assert.StartsWith("EVALUATE TOPN(10, SUMMARIZECOLUMNS(", dax);
        // The ordering comes in as a TOPN argument: it is what picks which ten groups get in.
        Assert.Contains("[Total], DESC", dax);
    }

    [Fact]
    public void OrderBy_ByExtensionColumn_UsesTheBracketedName()
    {
        string dax = Flat(PorCategoriaQuery().OrderByDescending(r => r.Total).ToDaxString());

        Assert.EndsWith("ORDER BY [Total] DESC", dax);
    }

    [Fact]
    public void OrderBy_ByKeyColumn_UsesTheTableQualifiedReference()
    {
        string dax = Flat(PorCategoriaQuery().OrderBy(r => r.Categoria).ToDaxString());

        Assert.EndsWith("ORDER BY Venda[Categoria] ASC", dax);
    }

    /// <summary>
    /// Both shapes coexist in a single ordering: the key comes out qualified by the table and the
    /// extension in brackets, because that is how <c>SUMMARIZECOLUMNS</c> produces them.
    /// </summary>
    [Fact]
    public void OrderBy_MixingKeyAndExtension_EmitsEachInItsOwnForm()
    {
        string dax = Flat(PorCategoriaQuery()
            .OrderByDescending(r => r.Total)
            .ThenBy(r => r.Categoria)
            .ToDaxString());

        Assert.EndsWith("ORDER BY [Total] DESC, Venda[Categoria] ASC", dax);
    }

    [Fact]
    public void OrderBy_AfterKeylessAggregate_OrdersByTheExtension()
    {
        string dax = Flat(Table()
            .Aggregate(g => new PorCategoria { Total = g.Sum(v => v.Valor) })
            .OrderByDescending(r => r.Total)
            .ToDaxString());

        Assert.EndsWith("ORDER BY [Total] DESC", dax);
    }

    [Fact]
    public void OrderBy_AfterProjection_OrdersByTheProjectedColumn()
    {
        string dax = Flat(Table()
            .Select(v => new Projetado { Categoria = v.Categoria, Valor = v.Valor })
            .OrderByDescending(r => r.Valor)
            .ToDaxString());

        // The output name comes from the property name, so the reference is [Valor] — and not
        // Venda[Valor], which SELECTCOLUMNS does not return.
        Assert.EndsWith("ORDER BY [Valor] DESC", dax);
    }

    [Fact]
    public void OrderBy_AfterWhereAndGrouping_KeepsTheFilterAsAFilterTable()
    {
        string dax = Flat(Table()
            .Where(v => v.Ativo)
            .GroupBy(v => v.Categoria)
            .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
            .OrderByDescending(r => r.Total)
            .Take(5)
            .ToDaxString());

        Assert.Contains("FILTER(Venda, Venda[Ativo])", dax);
        Assert.StartsWith("EVALUATE TOPN(5, SUMMARIZECOLUMNS(", dax);
        Assert.Contains("[Total], DESC", dax);
    }

    // ---------- the refusal that is left, and what it reports ----------

    /// <summary>
    /// The property exists on the result type and compiles, so the message has to say which columns
    /// the result carries — without that the caller has no way to find out the reason.
    /// </summary>
    [Fact]
    public void OrderBy_ByAColumnTheResultDoesNotCarry_NamesTheAvailableColumns()
    {
        // Regiao is neither a grouping key nor an extension column.
        DaxQuery<PorRegiao> agrupada = Table()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorRegiao { Regiao = g.Key, Total = g.Sum(v => v.Valor) });

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => agrupada.OrderBy(r => r.Regiao));

        Assert.Contains("PorRegiao.Regiao", ex.Message);
        Assert.Contains("Venda[Categoria]", ex.Message);
        Assert.Contains("[Total]", ex.Message);
    }

    /// <summary>Grouping result whose key is Categoria, but whose property is Regiao.</summary>
    private sealed class PorRegiao
    {
        [DaxColumn("Venda[Regiao]")] public string Regiao { get; set; } = "";
        [DaxColumn("[Total]")] public decimal Total { get; set; }
    }

    [Fact]
    public void TheRefusal_IsLocalized()
    {
        DaxQuery<PorRegiao> agrupada = PortugueseTable()
            .GroupBy(v => v.Categoria)
            .Select(g => new PorRegiao { Regiao = g.Key, Total = g.Sum(v => v.Valor) });

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => agrupada.OrderBy(r => r.Regiao));

        Assert.Contains("não é uma coluna do resultado da consulta", ex.Message);
    }

    /// <summary>
    /// An ordering declared <b>before</b> the grouping still requires a key column, and the message
    /// is a different one — there the problem is not the column missing from the result, it is the
    /// aggregation collapsing the rows and leaving no value per row to order by.
    /// </summary>
    [Fact]
    public void OrderBy_BeforeGrouping_StillRequiresAKeyColumn()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table()
                .OrderByDescending(v => v.Valor)
                .GroupBy(v => v.Categoria)
                .Select(g => new PorCategoria { Categoria = g.Key, Total = g.Sum(v => v.Valor) })
                .ToDaxString());

        Assert.Contains("not a grouping key", ex.Message);
    }

    /// <summary>
    /// The same check exists in the builder, not only in composition: <c>DaxPipelineBuilder</c> is
    /// public, so a hand-built pipeline ordering by something outside the result is refused too,
    /// instead of generating DAX the server rejects.
    /// </summary>
    [Fact]
    public void TheBuilder_RefusesAnOrderStageOutsideTheResultColumns()
    {
        var pipeline = new DaxPipeline("Venda", typeof(object))
        {
            Stages =
            [
                new DaxProjectStage([new DaxProjectionColumn("Categoria", new DaxColumnRef("Venda[Categoria]"))]),
                new DaxOrderStage(new DaxOrderTerm(new DaxColumnRef("Venda[Valor]"), false), ResetsOrder: true)
            ]
        };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => DaxPipelineBuilder.Build(pipeline));

        Assert.Contains("Venda[Valor]", ex.Message);
        Assert.Contains("[Categoria]", ex.Message);
    }
}
