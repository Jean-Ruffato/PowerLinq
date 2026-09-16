using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// A subtotal row alongside the detail rows, in the same query.
/// </summary>
/// <remarks>
/// <para>
/// <b>Summing the rows on the client is not equivalent.</b> It only coincides for an additive
/// measure. For a <c>DISTINCTCOUNT</c>, an average or a ratio, the engine's total is computed in
/// the total's filter context, and summing the rows gives the wrong number — plausible enough to
/// pass review. And a client-side total requires fetching <b>every</b> row, which pagination prevents.
/// </para>
/// <para>
/// The tests pin down the <b>DAX</b>. That the engine computes a non-additive measure's total
/// correctly is a property of DAX, not of this library — what belongs here is emitting the shape
/// that delegates that to it instead of summing on the client.
/// </para>
/// </remarks>
public sealed class DaxSubtotalTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[CnpjRaiz]")] public string CnpjRaiz { get; set; } = "";
        [DaxColumn("Venda[RazaoSocial]")] public string RazaoSocial { get; set; } = "";
        [DaxColumn("Venda[Valor]")] public decimal Valor { get; set; }
        [DaxColumn("Venda[Ativo]")] public bool Ativo { get; set; }
    }

    /// <summary>With the subtotal column declared, as <c>WithSubtotal</c> requires.</summary>
    private sealed class ComTotal
    {
        [DaxColumn("Venda[CnpjRaiz]")] public string CnpjRaiz { get; set; } = "";
        [DaxColumn("Venda[RazaoSocial]")] public string RazaoSocial { get; set; } = "";
        [DaxColumn("[Realizado]")] public decimal Realizado { get; set; }
        [DaxColumn("[is_total]")] public bool IsTotal { get; set; }
    }

    /// <summary>Without it — the contract that has to be refused.</summary>
    private sealed class SemTotal
    {
        [DaxColumn("Venda[CnpjRaiz]")] public string CnpjRaiz { get; set; } = "";
        [DaxColumn("[Realizado]")] public decimal Realizado { get; set; }
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

    // ---------- the DAX ----------

    [Fact]
    public void WithSubtotal_WrapsTheKeysInRollupAddIsSubtotal()
    {
        string dax = Flat(Table()
            .GroupBy(v => new { v.CnpjRaiz, v.RazaoSocial })
            .WithSubtotal()
            // The key columns come from the SUMMARIZECOLUMNS and are materialized by the contract's
            // attribute; the lambda assigns only the aggregates.
            .Select(g => new ComTotal { Realizado = g.Sum(v => v.Valor) })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS("
            + "ROLLUPADDISSUBTOTAL(ROLLUPGROUP(Venda[CnpjRaiz], Venda[RazaoSocial]), \"is_total\"), "
            + "\"Realizado\", SUM(Venda[Valor]))",
            dax);
    }

    /// <summary>
    /// <c>ROLLUPGROUP</c>, not <c>ROLLUP</c>: the two columns are <b>one</b> level, and return a
    /// single total row. A tax id and a company name are the same grain written twice — a subtotal
    /// per level would be a hierarchy nobody asked for.
    /// </summary>
    [Fact]
    public void TheKeys_AreOneLevelAndNotAHierarchy()
    {
        string dax = Table()
            .GroupBy(v => new { v.CnpjRaiz, v.RazaoSocial })
            .WithSubtotal()
            // The key columns come from the SUMMARIZECOLUMNS and are materialized by the contract's
            // attribute; the lambda assigns only the aggregates.
            .Select(g => new ComTotal { Realizado = g.Sum(v => v.Valor) })
            .ToDaxString();

        Assert.Contains("ROLLUPGROUP(", dax, StringComparison.Ordinal);
        Assert.DoesNotContain("ROLLUP(", dax, StringComparison.Ordinal);
    }

    /// <summary>Without <c>WithSubtotal</c> the keys stay raw — nothing changes.</summary>
    [Fact]
    public void WithoutSubtotal_TheKeysAreUnchanged()
    {
        string dax = Flat(Table()
            .GroupBy(v => v.CnpjRaiz)
            .Select(g => new ComTotal
            {
                CnpjRaiz = g.Key,
                Realizado = g.Sum(v => v.Valor)
            })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(Venda[CnpjRaiz], \"Realizado\", SUM(Venda[Valor]))",
            dax);
    }

    [Fact]
    public void WithSubtotal_KeepsTheFilterTables()
    {
        string dax = Flat(Table()
            .Where(v => v.Ativo)
            .GroupBy(v => v.CnpjRaiz)
            .WithSubtotal()
            .Select(g => new ComTotal
            {
                CnpjRaiz = g.Key,
                Realizado = g.Sum(v => v.Valor)
            })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS("
            + "ROLLUPADDISSUBTOTAL(ROLLUPGROUP(Venda[CnpjRaiz]), \"is_total\"), "
            + "FILTER(Venda, Venda[Ativo]), "
            + "\"Realizado\", SUM(Venda[Valor]))",
            dax);
    }

    /// <summary>
    /// The third criterion. There is no way to test here that the engine computes a non-additive
    /// measure's total correctly — that requires a real model, and it is a property of DAX, not of
    /// this library (see the class's <c>remarks</c>). What belongs at this level is confirming that
    /// <c>WithSubtotal</c> does not change shape when the measure is a <c>DISTINCTCOUNT</c>: the
    /// key goes inside <c>ROLLUPADDISSUBTOTAL</c>/<c>ROLLUPGROUP</c> and the measure stays a column
    /// of the <c>SUMMARIZECOLUMNS</c> like any other — nothing here sums anything on the client,
    /// which is the part that guarantees the non-additive total comes out right.
    /// </summary>
    [Fact]
    public void WithSubtotal_LeavesANonAdditiveMeasureForTheEngineToCompute()
    {
        string dax = Flat(Table()
            .GroupBy(v => v.CnpjRaiz)
            .WithSubtotal()
            .Select(g => new ComTotal
            {
                CnpjRaiz = g.Key,
                Realizado = g.CountDistinct(v => v.RazaoSocial)
            })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS("
            + "ROLLUPADDISSUBTOTAL(ROLLUPGROUP(Venda[CnpjRaiz]), \"is_total\"), "
            + "\"Realizado\", DISTINCTCOUNT(Venda[RazaoSocial]))",
            dax);
    }

    // ---------- the guard ----------

    /// <summary>
    /// The fourth criterion. Without the column declared, the total row would arrive as a
    /// <b>detail row with blank keys</b>: the grid would show an empty category carrying the
    /// total's value, and summing the column would give double. Materialization has no way to
    /// notice — it silently discards an unmapped column, which is the right behaviour for everything else.
    /// </summary>
    [Fact]
    public void AContractWithoutTheFlag_IsRefusedNamingWhatToAdd()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table()
                .GroupBy(v => v.CnpjRaiz)
                .WithSubtotal()
                .Select(g => new SemTotal
                {
                    CnpjRaiz = g.Key,
                    Realizado = g.Sum(v => v.Valor)
                }));

        Assert.Contains("SemTotal", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[is_total]", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The refusal happens during <b>composition</b>, not during execution.</summary>
    [Fact]
    public void TheRefusal_HappensBeforeAnyQueryIsSent()
    {
        var executor = new NoopExecutor();

        Assert.Throws<NotSupportedException>(
            () => new DaxTable<Venda>(executor)
                .GroupBy(v => v.CnpjRaiz)
                .WithSubtotal()
                .Select(g => new SemTotal { CnpjRaiz = g.Key, Realizado = g.Sum(v => v.Valor) }));
    }

    // ---------- the window ----------

    /// <summary>
    /// The fifth criterion. The total row is <b>not</b> a detail row, and <c>TOPN</c> does not know
    /// that: it would enter the page's count and show up in the middle of it, or vanish depending
    /// on the page requested. A grid would show the footer as if it were another category — the
    /// same error the <c>[is_total]</c> column exists to avoid, reintroduced by
    /// pagination.
    /// </summary>
    [Theory]
    [InlineData("take")]
    [InlineData("skip")]
    public void PagingASubtotalledGrouping_IsRefused(string operador)
    {
        DaxQuery<ComTotal> agrupada = Table()
            .GroupBy(v => v.CnpjRaiz)
            .WithSubtotal()
            .Select(g => new ComTotal { Realizado = g.Sum(v => v.Valor) });

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => (operador == "take"
                    ? agrupada.Take(5)
                    : agrupada.OrderBy(r => r.CnpjRaiz).Skip(5))
                .ToDaxString());

        Assert.Contains("subtotal", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Without a subtotal, paginating the grouping still holds.</summary>
    [Fact]
    public void PagingAPlainGrouping_StillWorks()
    {
        string dax = Table()
            .GroupBy(v => v.CnpjRaiz)
            .Select(g => new ComTotal { Realizado = g.Sum(v => v.Valor) })
            .Take(5)
            .ToDaxString();

        Assert.Contains("TOPN(5, SUMMARIZECOLUMNS(", Flat(dax), StringComparison.Ordinal);
    }

    // ---------- the column in the result ----------

    /// <summary>
    /// <c>[is_total]</c> enters the closed set the result carries, so it can be sorted and filtered
    /// by — it is how a grid separates the footer from the rows.
    /// </summary>
    [Fact]
    public void TheFlag_IsPartOfTheResultColumns()
    {
        string dax = Flat(Table()
            .GroupBy(v => v.CnpjRaiz)
            .WithSubtotal()
            .Select(g => new ComTotal { CnpjRaiz = g.Key, Realizado = g.Sum(v => v.Valor) })
            .Where(r => !r.IsTotal)
            .ToDaxString());

        Assert.Equal(
            "EVALUATE FILTER(SUMMARIZECOLUMNS("
            + "ROLLUPADDISSUBTOTAL(ROLLUPGROUP(Venda[CnpjRaiz]), \"is_total\"), "
            + "\"Realizado\", SUM(Venda[Valor])), NOT([is_total]))",
            dax);
    }
}
