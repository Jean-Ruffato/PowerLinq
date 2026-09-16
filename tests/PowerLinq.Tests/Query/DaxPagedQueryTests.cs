using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// The page and the whole set's total in a single query.
/// </summary>
/// <remarks>
/// <para>
/// Two round trips to the endpoint cost two connections, two capacity queues and two scans — and
/// they open an inconsistency window: a refresh between them returns a page from one state and a
/// total from another. The <c>ADDCOLUMNS</c> stamps the count onto every row of the page, and the
/// count comes out of a <c>VAR</c> so it is not re-evaluated per row.
/// </para>
/// <para>
/// The total is that of the set <b>before</b> the window. Counting after <c>Skip</c>/<c>Take</c>
/// would give the page size, which the caller already knows.
/// </para>
/// </remarks>
public sealed class DaxPagedQueryTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("Produto[Ativo]")] public bool Ativo { get; set; }
    }

    /// <summary>
    /// Raw executor: it records the DAX sent and returns the rows the test built. It is the analogue
    /// of the other files' <c>NoopExecutor</c>, with the capability pagination requires.
    /// </summary>
    private sealed class FakeRawExecutor(params DaxRow[] rows)
        : IDaxQueryExecutor, IDaxRawQueryExecutor
    {
        public string? LastQuery { get; private set; }

        /// <summary>How many round trips to the "endpoint" happened — what pagination exists to reduce.</summary>
        public int Queries { get; private set; }

        /// <summary>What the fallback count returns, when the page comes back empty.</summary>
        public long CountResult { get; init; }

        public Task<DaxResult> ExecuteRowsAsync(
            string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            Queries++;

            IReadOnlyList<string> columns =
                rows.Length == 0 ? [] : ["Produto[Id]", "Produto[Nome]", "[__pl_total]"];

            return Task.FromResult(new DaxResult(columns, rows));
        }

        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class
        {
            LastQuery = daxQuery;
            Queries++;
            return Task.FromResult(new List<T>());
        }

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            Queries++;
            return Task.FromResult<object?>(CountResult);
        }

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            Queries++;
            return Task.FromResult((int)CountResult);
        }
    }

    /// <summary>Executor without the capability — the refusal case.</summary>
    private sealed class PlainExecutor : IDaxQueryExecutor
    {
        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class => Task.FromResult(new List<T>());

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private static DaxRow Row(int id, string nome, long total) =>
        new(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Produto[Id]"] = id,
            ["Produto[Nome]"] = nome,
            ["[__pl_total]"] = total
        });

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    // ---------- the DAX ----------

    [Fact]
    public async Task APagedQuery_AsksForThePageAndTheTotalInOneQuery()
    {
        var executor = new FakeRawExecutor(Row(3, "Mesa", 87));

        _ = await new DaxTable<Produto>(executor)
            .Where(p => p.Ativo)
            .OrderBy(p => p.Id)
            .Skip(20)
            .Take(10)
            .ToPagedListAsync();

        Assert.Equal(
            "DEFINE VAR __pl_source_0 = FILTER(Produto, Produto[Ativo]) "
            + "VAR __pl_total = COUNTROWS(__pl_source_0) "
            + "EVALUATE ADDCOLUMNS("
            + "TOPN(10, EXCEPT(__pl_source_0, TOPN(20, __pl_source_0, Produto[Id], ASC)), "
            + "Produto[Id], ASC), \"[__pl_total]\", __pl_total) "
            + "ORDER BY Produto[Id] ASC",
            Flat(executor.LastQuery!));
    }

    /// <summary>
    /// The count comes out of a <c>VAR</c>, rather than written inside the <c>ADDCOLUMNS</c>: there
    /// it would be a calculated column's expression, evaluated <b>per row</b> of the page.
    /// </summary>
    [Fact]
    public async Task TheTotal_IsDeclaredOnceAndNotRecomputedPerRow()
    {
        var executor = new FakeRawExecutor(Row(1, "Mesa", 5));

        _ = await new DaxTable<Produto>(executor).OrderBy(p => p.Id).Skip(2).ToPagedListAsync();

        string dax = Flat(executor.LastQuery!);

        Assert.Equal(1, dax.Split("COUNTROWS(").Length - 1);
        Assert.Contains("VAR __pl_total = COUNTROWS(", dax, StringComparison.Ordinal);
        Assert.Contains("\"[__pl_total]\", __pl_total)", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only <c>Take</c>, no <c>Skip</c>: the source feeds the <c>TOPN</c> and the <c>COUNTROWS</c>,
    /// so it is declared too — <c>Take</c> alone does not declare in the ordinary query, because
    /// there the source appears only once.
    /// </summary>
    [Fact]
    public async Task TakeWithoutSkip_StillDeclaresTheSourceBeforeTheWindow()
    {
        var executor = new FakeRawExecutor(Row(1, "Mesa", 42));

        _ = await new DaxTable<Produto>(executor).Where(p => p.Ativo).Take(10).ToPagedListAsync();

        string dax = Flat(executor.LastQuery!);

        Assert.Equal(
            "DEFINE VAR __pl_source_0 = FILTER(Produto, Produto[Ativo]) "
            + "VAR __pl_total = COUNTROWS(__pl_source_0) "
            + "EVALUATE ADDCOLUMNS(TOPN(10, __pl_source_0), \"[__pl_total]\", __pl_total)",
            dax);
    }

    /// <summary>With no window at all, the total is the count of the result itself.</summary>
    [Fact]
    public async Task WithoutAWindow_TheTotalCountsTheResultItself()
    {
        var executor = new FakeRawExecutor(Row(1, "Mesa", 2));

        _ = await new DaxTable<Produto>(executor).Where(p => p.Ativo).ToPagedListAsync();

        string dax = Flat(executor.LastQuery!);

        Assert.Equal(
            "DEFINE VAR __pl_source_0 = FILTER(Produto, Produto[Ativo]) "
            + "VAR __pl_total = COUNTROWS(__pl_source_0) "
            + "EVALUATE ADDCOLUMNS(__pl_source_0, \"[__pl_total]\", __pl_total)",
            dax);
    }

    // ---------- the result ----------

    [Fact]
    public async Task ThePage_CarriesTheItemsAndTheTotal()
    {
        var executor = new FakeRawExecutor(Row(3, "Mesa", 87), Row(4, "Cadeira", 87));

        DaxPage<Produto> pagina = await new DaxTable<Produto>(executor)
            .OrderBy(p => p.Id).Skip(20).Take(10).ToPagedListAsync();

        Assert.Equal(87, pagina.Total);
        Assert.Equal([3, 4], pagina.Items.Select(p => p.Id));
        Assert.Equal("Mesa", pagina.Items[0].Nome);
    }

    /// <summary>
    /// The third criterion: a <c>Skip</c> past the end returns an empty page with the
    /// <b>right</b> total, greater than zero.
    /// </summary>
    /// <remarks>
    /// <c>ADDCOLUMNS</c> stamps the total onto each row of the page — with no row, there is no stamp.
    /// The shape proposed at the outset does not express this case, so the empty page pays for a
    /// second round trip, with the same count <c>CountAsync</c> emits. The normal case is still a
    /// single query; this is the degenerate exception.
    /// </remarks>
    [Fact]
    public async Task SkipBeyondTheEnd_ReturnsAnEmptyPageWithTheRightTotal()
    {
        var executor = new FakeRawExecutor { CountResult = 87 };

        DaxPage<Produto> pagina = await new DaxTable<Produto>(executor)
            .OrderBy(p => p.Id).Skip(1000).Take(10).ToPagedListAsync();

        Assert.Empty(pagina.Items);
        Assert.Equal(87, pagina.Total);

        // The second round trip is the count, and it counts the set — not the empty window.
        Assert.Contains("COUNTROWS(", executor.LastQuery!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fifth criterion: the round trips to the endpoint, before and after, in the same grid case.
    /// </summary>
    /// <remarks>
    /// Two round trips cost two connections, two capacity queues and two scans — and they open an
    /// inconsistency window, because a refresh between them returns a page from one state and a
    /// total from another. Measured here, and not in BenchmarkDotNet, because what is measured is a
    /// <b>count</b>: the time would be the network's and the capacity's, which no local benchmark reproduces.
    /// </remarks>
    [Fact]
    public async Task ThePagedQuery_HalvesTheRoundTripsOfThePageAndCountPair()
    {
        var antes = new FakeRawExecutor(Row(3, "Mesa", 87)) { CountResult = 87 };

        _ = await new DaxTable<Produto>(antes).OrderBy(p => p.Id).Skip(20).Take(10).ToListAsync();
        _ = await new DaxTable<Produto>(antes).OrderBy(p => p.Id).LongCountAsync();

        var depois = new FakeRawExecutor(Row(3, "Mesa", 87)) { CountResult = 87 };

        DaxPage<Produto> pagina = await new DaxTable<Produto>(depois)
            .OrderBy(p => p.Id).Skip(20).Take(10).ToPagedListAsync();

        Assert.Equal(2, antes.Queries);
        Assert.Equal(1, depois.Queries);

        // And the single round trip brings both, which is the point — one trip is no use if half is missing.
        Assert.NotEmpty(pagina.Items);
        Assert.Equal(87, pagina.Total);
    }

    // ---------- the refusal ----------

    [Fact]
    public async Task AnExecutorWithoutTheRawCapability_IsRefusedNamingIt()
    {
        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => new DaxTable<Produto>(new PlainExecutor()).ToPagedListAsync());

        Assert.Contains("IDaxRawQueryExecutor", ex.Message, StringComparison.Ordinal);
    }
}
