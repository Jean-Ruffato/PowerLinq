using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Hierarchical rollup: two or more subtotal levels, each with its own flag, in the same query —
/// <c>ROLLUPADDISSUBTOTAL(ROLLUPGROUP(...), "flag1", ROLLUPGROUP(...), "flag2")</c>.
/// </summary>
/// <remarks>
/// It complements <see cref="DaxSubtotalTests"/>, which covers the single level. What changes here
/// is composing several levels: the order of the <c>WithSubtotal</c> calls becomes the order of the
/// arguments in <c>ROLLUPADDISSUBTOTAL</c>, and each level needs its own flag column in the result
/// contract.
/// </remarks>
public sealed class DaxHierarchicalSubtotalTests
{
    [DaxTable("Fato")]
    private sealed class Fato
    {
        [DaxColumn("Fato[PeriodoId]")] public int PeriodoId { get; set; }
        [DaxColumn("Fato[PeriodoNome]")] public string PeriodoNome { get; set; } = "";
        [DaxColumn("Fato[CnpjRaiz]")] public string CnpjRaiz { get; set; } = "";
        [DaxColumn("Fato[RazaoSocial]")] public string RazaoSocial { get; set; } = "";
        [DaxColumn("Fato[Valor]")] public decimal Valor { get; set; }
    }

    /// <summary>With both flags declared, as the two levels require.</summary>
    private sealed class ComDoisNiveis
    {
        [DaxColumn("Fato[PeriodoId]")] public int PeriodoId { get; set; }
        [DaxColumn("Fato[PeriodoNome]")] public string PeriodoNome { get; set; } = "";
        [DaxColumn("Fato[CnpjRaiz]")] public string CnpjRaiz { get; set; } = "";
        [DaxColumn("Fato[RazaoSocial]")] public string RazaoSocial { get; set; } = "";
        [DaxColumn("[Realizado]")] public decimal Realizado { get; set; }
        [DaxColumn("[is_period_total]")] public bool IsPeriodTotal { get; set; }
        [DaxColumn("[is_exporter_total]")] public bool IsExporterTotal { get; set; }
    }

    /// <summary>Only the outer level's flag — the inner one is missing.</summary>
    private sealed class SoUmaFlag
    {
        [DaxColumn("Fato[PeriodoId]")] public int PeriodoId { get; set; }
        [DaxColumn("[Realizado]")] public decimal Realizado { get; set; }
        [DaxColumn("[is_period_total]")] public bool IsPeriodTotal { get; set; }
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

    private static DaxTable<Fato> Table() => new(new NoopExecutor());

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    // ---------- the DAX ----------

    [Fact]
    public void TwoLevels_ProduceOneRollupAddIsSubtotalWithTwoRollupGroups()
    {
        string dax = Flat(Table()
            .GroupBy(f => new { f.PeriodoId, f.PeriodoNome, f.CnpjRaiz, f.RazaoSocial })
            .WithSubtotal("is_period_total", f => new { f.PeriodoId, f.PeriodoNome })
            .WithSubtotal("is_exporter_total", f => new { f.CnpjRaiz, f.RazaoSocial })
            .Select(g => new ComDoisNiveis { Realizado = g.Sum(f => f.Valor) })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS("
            + "ROLLUPADDISSUBTOTAL("
            + "ROLLUPGROUP(Fato[PeriodoId], Fato[PeriodoNome]), \"is_period_total\", "
            + "ROLLUPGROUP(Fato[CnpjRaiz], Fato[RazaoSocial]), \"is_exporter_total\"), "
            + "\"Realizado\", SUM(Fato[Valor]))",
            dax);
    }

    /// <summary>The first call is the outer level: reversing the order reverses the generated DAX.</summary>
    [Fact]
    public void CallOrder_IsTheHierarchyOrder()
    {
        string dax = Flat(Table()
            .GroupBy(f => new { f.PeriodoId, f.PeriodoNome, f.CnpjRaiz, f.RazaoSocial })
            .WithSubtotal("is_exporter_total", f => new { f.CnpjRaiz, f.RazaoSocial })
            .WithSubtotal("is_period_total", f => new { f.PeriodoId, f.PeriodoNome })
            .Select(g => new ComDoisNiveis { Realizado = g.Sum(f => f.Valor) })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS("
            + "ROLLUPADDISSUBTOTAL("
            + "ROLLUPGROUP(Fato[CnpjRaiz], Fato[RazaoSocial]), \"is_exporter_total\", "
            + "ROLLUPGROUP(Fato[PeriodoId], Fato[PeriodoNome]), \"is_period_total\"), "
            + "\"Realizado\", SUM(Fato[Valor]))",
            dax);
    }

    [Fact]
    public void HierarchicalSubtotal_KeepsTheFilterTables()
    {
        string dax = Flat(Table()
            .Where(f => f.Valor > 0)
            .GroupBy(f => new { f.PeriodoId, f.CnpjRaiz })
            .WithSubtotal("is_period_total", f => f.PeriodoId)
            .WithSubtotal("is_exporter_total", f => f.CnpjRaiz)
            .Select(g => new ComDoisNiveis { Realizado = g.Sum(f => f.Valor) })
            .ToDaxString());

        Assert.Contains("ROLLUPADDISSUBTOTAL(", dax, StringComparison.Ordinal);
        Assert.Contains("FILTER(Fato, Fato[Valor] > 0)", dax, StringComparison.Ordinal);
    }

    // ---------- the guard for a column outside the key ----------

    /// <summary>The third criterion: a level outside the key is refused during composition.</summary>
    [Fact]
    public void ALevelOverAColumnNotInTheGroupKey_IsRefusedNamingIt()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table()
                .GroupBy(f => f.PeriodoId)
                .WithSubtotal("is_exporter_total", f => f.CnpjRaiz));

        Assert.Contains("Fato[CnpjRaiz]", ex.Message, StringComparison.Ordinal);
    }

    // ---------- the flag name guards ----------

    [Fact]
    public void ADuplicateFlagName_IsRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table()
                .GroupBy(f => new { f.PeriodoId, f.CnpjRaiz })
                .WithSubtotal("is_total", f => f.PeriodoId)
                .WithSubtotal("is_total", f => f.CnpjRaiz));

        Assert.Contains("is_total", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyFlagName_IsRefused()
    {
        Assert.Throws<ArgumentException>(
            () => Table()
                .GroupBy(f => f.PeriodoId)
                .WithSubtotal("   ", f => f.PeriodoId));
    }

    /// <summary>
    /// <c>WithSubtotal()</c> with no arguments covers the whole key as a single level; combining it
    /// with an explicit level is redundant and is refused, instead of generating a meaningless
    /// hierarchy.
    /// </summary>
    [Fact]
    public void TheWholeKeyOverload_CannotFollowALeveledCall()
    {
        Assert.Throws<NotSupportedException>(
            () => Table()
                .GroupBy(f => new { f.PeriodoId, f.CnpjRaiz })
                .WithSubtotal("is_period_total", f => f.PeriodoId)
                .WithSubtotal());
    }

    // ---------- the guard for the column in the result ----------

    /// <summary>Each level requires its own flag; a missing one is refused while naming which.</summary>
    [Fact]
    public void AContractMissingOneOfTheFlags_IsRefusedNamingIt()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table()
                .GroupBy(f => f.PeriodoId)
                .WithSubtotal("is_period_total", f => f.PeriodoId)
                .WithSubtotal("is_exporter_total", f => f.PeriodoId)
                .Select(g => new SoUmaFlag
                {
                    PeriodoId = g.Key,
                    Realizado = g.Sum(f => f.Valor)
                }));

        Assert.Contains("SoUmaFlag", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[is_exporter_total]", ex.Message, StringComparison.Ordinal);
    }

    // ---------- the window ----------

    /// <summary>The refusal to paginate a grouping with a subtotal holds for a hierarchy too.</summary>
    [Fact]
    public void PagingAHierarchicalSubtotal_IsRefused()
    {
        DaxQuery<ComDoisNiveis> agrupada = Table()
            .GroupBy(f => new { f.PeriodoId, f.CnpjRaiz })
            .WithSubtotal("is_period_total", f => f.PeriodoId)
            .WithSubtotal("is_exporter_total", f => f.CnpjRaiz)
            .Select(g => new ComDoisNiveis { Realizado = g.Sum(f => f.Valor) });

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => agrupada.Take(5).ToDaxString());

        Assert.Contains("subtotal", ex.Message, StringComparison.Ordinal);
    }
}
