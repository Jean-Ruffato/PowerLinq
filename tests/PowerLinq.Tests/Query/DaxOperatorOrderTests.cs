using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Composition no longer has a fixed order: the <c>DaxPipeline</c> keeps the operators in the
/// sequence they were applied in, so each order has its own translation.
/// </summary>
/// <remarks>
/// <para>
/// This file used to be about <b>refusals</b>. The flat <c>DaxQueryDefinition</c> kept
/// <c>Filter</c>, <c>TopN</c>, <c>Skip</c> and <c>OrderBy</c> as independent fields, with no order
/// among them, so <c>Take(5).Where(p)</c> and <c>Where(p).Take(5)</c> were the same value —
/// refusing was preferable to silently generating the DAX of one when the other was asked for.
/// </para>
/// <para>
/// With the reshape the tests became what they should always have been: <b>each order generates
/// the DAX it means</b>. A single refusal is left, grouping after a window, and its nature is
/// different — it is not the structure failing to represent it, it is <c>SUMMARIZECOLUMNS</c>
/// having nowhere to take a <c>TOPN</c>.
/// </para>
/// </remarks>
public sealed class DaxOperatorOrderTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("Produto[Ativo]")] public bool Ativo { get; set; }
    }

    private sealed class Projetado
    {
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
    }

    private sealed class Total
    {
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

    private static DaxTable<Produto> Table() => new(new NoopExecutor());

    // The constructor that takes a localizer is internal, so the pt-BR table comes
    // through the public factory — which is how the DI container builds it.
    private static IDaxTable<Produto> PortugueseTable() =>
        new DaxTableFactory(new ResourceManagerPowerLinqLocalizer("pt-BR"))
            .Create<Produto>(new NoopExecutor());

    /// <summary>
    /// Collapses the writer's formatting: the subject here is which operator wraps which, not the
    /// indentation — which <c>DaxQueryBuilderTests</c> pins down character by character.
    /// </summary>
    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    // ---------- after Take: now translated ----------
    //
    // These cases used to be refused. The refusal did not come from DAX — it came from the flat
    // DaxQueryDefinition not distinguishing Take(5).Where(p) from Where(p).Take(5), so refusing
    // was preferable to silently generating the other one's DAX. With the pipeline of ordered
    // stages the distinction exists, and each of these has a translation of its own.

    [Fact]
    public void Where_AfterTake_FiltersTheWindow_NotTheTable()
    {
        string dax = Flat(Table().Take(5).Where(p => p.Ativo).ToDaxString());

        // The FILTER sits OUTSIDE the TOPN: limit five rows, then filter those five.
        Assert.Equal("EVALUATE FILTER(TOPN(5, Produto), Produto[Ativo])", dax);
    }

    /// <summary>
    /// The two orders generate different DAX — which is the reshape's reason for existing. One of
    /// them used to be refused precisely because it would produce the other one's DAX.
    /// </summary>
    [Fact]
    public void WhereBeforeTake_AndWhereAfterTake_AreDifferentQueries()
    {
        string antes = Flat(Table().Where(p => p.Ativo).Take(5).ToDaxString());
        string depois = Flat(Table().Take(5).Where(p => p.Ativo).ToDaxString());

        Assert.Equal("EVALUATE TOPN(5, FILTER(Produto, Produto[Ativo]))", antes);
        Assert.Equal("EVALUATE FILTER(TOPN(5, Produto), Produto[Ativo])", depois);
        Assert.NotEqual(antes, depois);
    }

    /// <summary>
    /// The case that forced moving predicate merging from composition to writing. Merging during
    /// composition — as the flat definition did, with <c>&amp;&amp;</c> — would make
    /// <c>Where(a).Take(5).Where(b)</c> indistinguishable from <c>Where(a &amp;&amp; b).Take(5)</c>,
    /// and the second filters the whole table before limiting.
    /// </summary>
    [Fact]
    public void WhereAroundTake_IsNotTheSameAsTheConjunction()
    {
        string aoRedor = Flat(Table()
            .Where(p => p.Ativo)
            .Take(5)
            .Where(p => p.Nome == "x")
            .ToDaxString());

        string fundido = Flat(Table()
            .Where(p => p.Ativo && p.Nome == "x")
            .Take(5)
            .ToDaxString());

        Assert.Equal(
            "EVALUATE FILTER(TOPN(5, FILTER(Produto, Produto[Ativo])), Produto[Nome] = \"x\")",
            aoRedor);
        Assert.Equal(
            "EVALUATE TOPN(5, FILTER(Produto, Produto[Ativo] && Produto[Nome] = \"x\"))",
            fundido);
    }

    [Theory]
    [InlineData("OrderBy", "ASC")]
    [InlineData("OrderByDescending", "DESC")]
    [InlineData("ThenBy", "ASC")]
    [InlineData("ThenByDescending", "DESC")]
    public void Ordering_AfterTake_OrdersTheWindow(string @operator, string direction)
    {
        DaxQuery<Produto> janela = Table().Take(5);

        DaxQuery<Produto> ordenada = @operator switch
        {
            "OrderBy" => janela.OrderBy(p => p.Nome),
            "OrderByDescending" => janela.OrderByDescending(p => p.Nome),
            "ThenBy" => janela.ThenBy(p => p.Nome),
            _ => janela.ThenByDescending(p => p.Nome)
        };

        // The ordering survives as the EVALUATE's ORDER BY: it sorts what the window returned, and
        // does not choose which rows go into it.
        Assert.Equal(
            $"EVALUATE TOPN(5, Produto) ORDER BY Produto[Nome] {direction}",
            Flat(ordenada.ToDaxString()));
    }

    [Fact]
    public void Take_AfterTake_NestsTheWindows()
    {
        // Take(5).Take(10) returns 5 rows in LINQ, and TOPN(10, TOPN(5, ...)) returns 5 in DAX.
        // It used to be refused because the flat definition had a single field, and the 10 would overwrite the 5.
        Assert.Equal(
            "EVALUATE TOPN(10, TOPN(5, Produto))",
            Flat(Table().Take(5).Take(10).ToDaxString()));
    }

    /// <summary>
    /// <c>Skip</c> after <c>Take</c> paginates the window. It used to require the ordering
    /// <b>redeclared</b> while <c>Take</c> consumed the pending terms; consuming them stopped,
    /// because <c>TOPN</c> does not guarantee the output's order and the <c>EVALUATE</c>'s
    /// <c>ORDER BY</c> clause needs them.
    /// </summary>
    [Fact]
    public void Skip_AfterTake_PagesTheWindow()
    {
        string dax = Flat(Table().OrderBy(p => p.Id).Take(5).Skip(2).ToDaxString());

        // The window first, and the EXCEPT over it: rows 3 to 5 of the first five. The window is
        // declared once and referenced twice — before the VAR, the TOPN(5, ...) appeared whole on
        // both sides of the EXCEPT.
        Assert.Equal(
            "DEFINE VAR __pl_source_0 = TOPN(5, Produto, Produto[Id], ASC) "
            + "EVALUATE EXCEPT(__pl_source_0, TOPN(2, __pl_source_0, Produto[Id], ASC)) "
            + "ORDER BY Produto[Id] ASC",
            dax);

        // The window's TOPN appears ONCE in the text: that is what the declaration exists to guarantee.
        Assert.Equal(1, dax.Split("TOPN(5,").Length - 1);
    }

    /// <summary>
    /// <c>OrderBy</c> <b>replaces</b> the ordering, as in LINQ; the one that adds a criterion is
    /// <c>ThenBy</c>. The flat definition had only a list of terms and appended in both cases, so
    /// <c>OrderBy(a).OrderBy(b)</c> generated <c>ORDER BY a, b</c> — wrong, but unreachable in
    /// practice. It became reachable now that ordering after a window is allowed, so the semantics
    /// were fixed along with it.
    /// </summary>
    [Fact]
    public void OrderBy_AfterOrderBy_Replaces_WhileThenBy_Appends()
    {
        string substituido = Flat(Table().OrderBy(p => p.Id).OrderBy(p => p.Nome).ToDaxString());
        string acrescentado = Flat(Table().OrderBy(p => p.Id).ThenBy(p => p.Nome).ToDaxString());

        Assert.Equal("EVALUATE Produto ORDER BY Produto[Nome] ASC", substituido);
        Assert.Equal("EVALUATE Produto ORDER BY Produto[Id] ASC, Produto[Nome] ASC", acrescentado);
    }

    // ---------- still refused: the grouping ----------
    //
    // It is the refusal that REMAINS, and its nature is different. It is not about representation:
    // the pipeline represents Take(5).GroupBy(k) with no trouble. It is about generation — the
    // SUMMARIZECOLUMNS takes grouping columns and filter tables, and has nowhere to take a TOPN, so
    // grouping after a window would discard it silently.

    [Fact]
    public void GroupBy_AfterTake_Throws()
    {
        // The family's worst case: SUMMARIZECOLUMNS only uses the filter, so the Take
        // disappeared without a trace and the aggregation scanned the whole table.
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table().Take(5).GroupBy(p => p.Nome));

        Assert.StartsWith("GroupBy cannot be applied after Take", ex.Message);
    }

    [Fact]
    public void Aggregate_AfterTake_Throws()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table().Take(5).Aggregate(g => new Total { Qtd = g.Count() }));

        Assert.StartsWith("Aggregate cannot be applied after Take", ex.Message);
    }

    /// <summary>
    /// <c>AnyAsync(predicate)</c> and <c>AllAsync</c> compose a <c>Where</c> internally, and now
    /// that simply works after a window: counting the window's rows that satisfy the predicate is a
    /// well-formed question.
    /// </summary>
    [Fact]
    public async Task AnyAsyncWithPredicate_AfterTake_AsksAboutTheWindow()
    {
        var executor = new RecordingExecutor();

        await new DaxTable<Produto>(executor).Take(5).AnyAsync(p => p.Ativo);

        Assert.Equal(
            "EVALUATE ROW(\"[Count]\", COUNTROWS(FILTER(TOPN(5, Produto), Produto[Ativo])))",
            Flat(executor.LastQuery!));
    }

    [Fact]
    public async Task AllAsync_AfterTake_AsksAboutTheWindow()
    {
        var executor = new RecordingExecutor();

        await new DaxTable<Produto>(executor).Take(5).AllAsync(p => p.Ativo);

        // All is the negation of Any of the negated predicate: it counts the window's rows that do NOT match.
        Assert.Equal(
            "EVALUATE ROW(\"[Count]\", COUNTROWS(FILTER(TOPN(5, Produto), NOT(Produto[Ativo]))))",
            Flat(executor.LastQuery!));
    }

    // ---------- after Skip: now translated ----------

    [Fact]
    public void Where_AfterSkip_FiltersThePage()
    {
        string dax = Flat(Table().OrderBy(p => p.Id).Skip(10).Where(p => p.Ativo).ToDaxString());

        Assert.StartsWith("EVALUATE FILTER(EXCEPT(", dax);
        Assert.EndsWith("Produto[Ativo]) ORDER BY Produto[Id] ASC", dax);
    }

    [Fact]
    public void Skip_AfterSkip_PagesTwice()
    {
        // Skip does not consume the ordering, so the second Skip still has it — and Skip(10).Skip(5)
        // discards 15 rows, as in LINQ.
        string dax = Flat(Table().OrderBy(p => p.Id).Skip(10).Skip(5).ToDaxString());

        Assert.Equal(
            "DEFINE VAR __pl_source_0 = EXCEPT(Produto, TOPN(10, Produto, Produto[Id], ASC)) "
            + "EVALUATE EXCEPT(__pl_source_0, TOPN(5, __pl_source_0, Produto[Id], ASC)) "
            + "ORDER BY Produto[Id] ASC",
            dax);

        // TWO occurrences, not three. `EXCEPT(source, TOPN(n, source, ...))` references the source
        // twice, so before the VAR the first Skip's EXCEPT appeared duplicated in the text and each
        // chained Skip multiplied the subtree.
        Assert.Equal(2, dax.Split("EXCEPT(").Length - 1);
    }

    [Fact]
    public void OrderBy_AfterSkip_ReordersThePage()
    {
        string dax = Flat(Table().OrderBy(p => p.Id).Skip(10).OrderBy(p => p.Nome).ToDaxString());

        // The EXCEPT used Id — it was the ordering in force when it was reached — and the result
        // comes out sorted by Nome, because the following OrderBy replaced the ordering.
        Assert.Contains("Produto[Id], ASC", dax);
        Assert.EndsWith("ORDER BY Produto[Nome] ASC", dax);
    }

    [Fact]
    public void GroupBy_AfterSkip_Throws() =>
        Assert.Throws<NotSupportedException>(
            () => Table().OrderBy(p => p.Id).Skip(10).GroupBy(p => p.Nome));

    // ---------- the canonical order: still valid ----------

    [Fact]
    public void CanonicalOrder_FiltersOrdersSkipsAndTakes()
    {
        string dax = Table()
            .Where(p => p.Ativo)
            .OrderBy(p => p.Id)
            .Skip(10)
            .Take(5)
            .ToDaxString();

        Assert.Contains("TOPN(", dax);
        Assert.Contains("EXCEPT(", dax);
        Assert.Contains("FILTER(", dax);
    }

    [Fact]
    public void MultipleWhere_BeforeTake_StillComposes()
    {
        string dax = Table()
            .Where(p => p.Ativo)
            .Where(p => p.Id > 10)
            .Take(5)
            .ToDaxString();

        Assert.Contains("&&", dax);
        Assert.Contains("TOPN(", dax);
    }

    [Fact]
    public void ThenBy_AfterOrderBy_StillComposes()
    {
        string dax = Table()
            .OrderBy(p => p.Id)
            .ThenByDescending(p => p.Nome)
            .ToDaxString();

        Assert.Contains("ORDER BY Produto[Id] ASC, Produto[Nome] DESC", dax);
    }

    [Fact]
    public void Select_AfterTake_IsAllowedBecauseProjectionPreservesSemantics()
    {
        // SELECTCOLUMNS(TOPN(5, ...)) corresponds to Take(5).Select(...) in LINQ.
        string dax = Table()
            .Take(5)
            .Select(p => new Projetado { Nome = p.Nome })
            .ToDaxString();

        Assert.Contains("SELECTCOLUMNS(TOPN(5, Produto)", dax);
    }

    [Fact]
    public async Task CountAsync_AfterTake_CountsTheWindow()
    {
        var executor = new RecordingExecutor();
        var table = new DaxTable<Produto>(executor);

        await table.Take(5).CountAsync();

        Assert.Contains("COUNTROWS(TOPN(5, Produto))", executor.LastQuery);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_AfterTake_NarrowsToOneRow()
    {
        var executor = new RecordingExecutor();
        var table = new DaxTable<Produto>(executor);

        await table.Take(5).FirstOrDefaultAsync();

        // Take(1) is one more stage, so the DAX is a nested TOPN — and the first of the five is the
        // first, which is what Take(5).First() means.
        Assert.Equal("EVALUATE TOPN(1, TOPN(5, Produto))", Flat(executor.LastQuery!));
    }

    [Fact]
    public void WhereIf_WithFalseCondition_AfterTake_ComposesNothing()
    {
        string dax = Table().Take(5).WhereIf(false, p => p.Ativo).ToDaxString();

        Assert.Contains("TOPN(", dax);
        Assert.DoesNotContain("FILTER(", dax);
    }

    [Fact]
    public void WhereIf_WithTrueCondition_AfterTake_FiltersTheWindow() =>
        Assert.Equal(
            "EVALUATE FILTER(TOPN(5, Produto), Produto[Ativo])",
            Flat(Table().Take(5).WhereIf(true, p => p.Ativo).ToDaxString()));

    // ---------- message ----------
    //
    // The window message survived, but the only operator that produces it now is the grouping.

    [Fact]
    public void Message_IsLocalized()
    {
        NotSupportedException english = Assert.Throws<NotSupportedException>(
            () => Table().Take(5).GroupBy(p => p.Nome));
        NotSupportedException portuguese = Assert.Throws<NotSupportedException>(
            () => PortugueseTable().Take(5).GroupBy(p => p.Nome));

        Assert.StartsWith("GroupBy cannot be applied after Take", english.Message);
        Assert.StartsWith("GroupBy não pode ser aplicado depois de Take", portuguese.Message);
    }

    [Fact]
    public void Message_NamesTheSupportedOrder()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table().Take(5).GroupBy(p => p.Nome));

        Assert.Contains("Where, OrderBy, Skip, Take", ex.Message);
    }

    /// <summary>
    /// The projection carries the pipeline, so it composes over any order — including the one that
    /// was refused until the previous slice.
    /// </summary>
    [Fact]
    public void Select_AfterANonCanonicalComposition_ProjectsTheFilteredWindow()
    {
        string dax = Flat(Table()
            .Take(5)
            .Where(p => p.Ativo)
            .Select(p => new Projetado { Nome = p.Nome })
            .ToDaxString());

        // The output name is "Produto[Nome]" because this file's DTO declares
        // [DaxColumn("Produto[Nome]")], and a reference that is not in brackets goes through
        // verbatim — behaviour that predates this change. What matters here is the projection's SOURCE.
        Assert.Equal(
            "EVALUATE SELECTCOLUMNS(FILTER(TOPN(5, Produto), Produto[Ativo]), \"Produto[Nome]\", Produto[Nome])",
            dax);
    }

    /// <summary>
    /// The bridge to the terminals that do <b>not yet</b> carry the pipeline refuses instead of
    /// approximating. Converting <c>Take(5).Where(p)</c> into a flat definition would produce the
    /// DAX of <c>Where(p).Take(5)</c> — the defect the pipeline exists to eliminate. What remains is
    /// the grouping, which is also refused because of the window; the bridge's pure case is a
    /// <c>Where</c> after a <c>Skip</c>, which is not the canonical order and is not a window as far
    /// as <c>Aggregate</c> is concerned.
    /// </summary>
    [Fact]
    public void Aggregate_ThroughTheBridge_RefusesNonCanonicalOrderInsteadOfApproximating()
    {
        // Where after Skip: representable in the pipeline, but no flat definition expresses it.
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table().OrderBy(p => p.Id).Skip(10).Where(p => p.Ativo)
                         .Aggregate(g => new Total { Qtd = g.Count() }));

        // The window is detected first, so the message is the window one — and the test records that
        // the refusal happens, with whichever reason comes first.
        Assert.Contains("Aggregate", ex.Message);
    }

    // ---------- invalid arguments ----------

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Take_WithNegativeCount_Throws(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Table().Take(count));

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Skip_WithNegativeCount_Throws(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Table().Skip(count));

    private sealed class RecordingExecutor : IDaxQueryExecutor
    {
        public string? LastQuery { get; private set; }

        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class
        {
            LastQuery = daxQuery;
            return Task.FromResult(new List<T>());
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
}
