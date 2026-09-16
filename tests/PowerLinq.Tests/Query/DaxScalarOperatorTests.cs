using System.Globalization;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Execution operators that end the query in a <b>scalar</b> — <c>SumAsync</c>, <c>MinAsync</c>,
/// <c>MaxAsync</c>, <c>AverageAsync</c>, <c>LongCountAsync</c> — and the ones that end in a single
/// row or in a collection — <c>SingleAsync</c>, <c>ToArrayAsync</c>,
/// <c>ToDictionaryAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// The axis of these tests is <b>where the filter goes in</b>. DAX's scalar form
/// (<c>SUM(Produto[Preco])</c>) sums in the current filter context, which in a query without a
/// <c>CALCULATE</c> is the whole model — the query's <c>FILTER</c> would be ignored and the sum
/// would come from the whole table, <b>with no error</b>. The iterating form
/// (<c>SUMX(&lt;source&gt;, ...)</c>) opens a row context over the already-filtered source. Both
/// DAX forms are valid; only one answers the question LINQ asked.
/// </para>
/// <para>
/// The second axis is <b>BLANK</b>. Every DAX iterating aggregate returns <c>BLANK</c> over an
/// empty table — including <c>SUMX</c>, which does not return zero. It is that <c>BLANK</c> that
/// separates "summed to zero" from "there was no row", and therefore decides between returning zero, returning null and throwing.
/// </para>
/// </remarks>
public sealed class DaxScalarOperatorTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("Produto[Preco]")] public decimal Preco { get; set; }
        [DaxColumn("Produto[Quantidade]")] public int Quantidade { get; set; }
        [DaxColumn("Produto[Ativo]")] public bool Ativo { get; set; }
    }

    /// <summary>
    /// Records the DAX and returns a scripted scalar. <see cref="Scalar"/> is an <c>object?</c> on
    /// purpose: <see langword="null"/> is the server's <c>BLANK</c>, and it is the fake's
    /// <b>default</b> value — the empty case is the one that most needs testing.
    /// </summary>
    private sealed class RecordingExecutor(object? scalar = null, int rows = 0) : IDaxQueryExecutor
    {
        public string? LastQuery { get; private set; }

        public object? Scalar { get; set; } = scalar;

        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class
        {
            LastQuery = daxQuery;
            return Task.FromResult(Enumerable.Range(0, rows).Select(_ => Activator.CreateInstance<T>()).ToList());
        }

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            return Task.FromResult(Scalar);
        }

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            return Task.FromResult(Scalar is null ? 0 : Convert.ToInt32(Scalar, CultureInfo.InvariantCulture));
        }
    }

    private static DaxTable<Produto> Table(RecordingExecutor executor) => new(executor);

    /// <summary>
    /// Collapses <c>DaxWriter</c>'s formatting: line breaks and indentation become a single space,
    /// and the space next to a parenthesis goes away.
    /// </summary>
    /// <remarks>
    /// This file's subject is <b>which function wraps which source</b>, not how the text is
    /// indented — that is already pinned down by <c>DaxQueryBuilderTests</c>, which compares the
    /// formatted string character by character. Without the flattening, a structural test would
    /// fail when a node's indentation changed, pointing at the wrong place.
    /// </remarks>
    private static string Flat(string? dax)
    {
        string collapsed = string.Join(
            ' ', (dax ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    // The constructor with a localizer is internal, so the pt-BR table comes through the public factory.
    private static IDaxTable<Produto> PortugueseTable(RecordingExecutor executor) =>
        new DaxTableFactory(new ResourceManagerPowerLinqLocalizer("pt-BR")).Create<Produto>(executor);

    // ---------- the iterating form, and the source it walks ----------

    [Fact]
    public async Task SumAsync_EmitsIteratorFormOverTable()
    {
        var executor = new RecordingExecutor();

        await Table(executor).SumAsync(p => p.Preco);

        Assert.Equal(
            "EVALUATE ROW(\"[Value]\", SUMX(Produto, Produto[Preco]))",
            Flat(executor.LastQuery));
    }

    /// <summary>
    /// The test that justifies choosing the iterating form: the <c>FILTER</c> has to be
    /// <b>inside</b> the <c>SUMX</c>. If the generated DAX were <c>SUM(Produto[Preco])</c>, the
    /// server would accept it and it would sum the whole table.
    /// </summary>
    [Fact]
    public async Task SumAsync_AfterWhere_SumsTheFilteredSource()
    {
        var executor = new RecordingExecutor();

        await Table(executor).Where(p => p.Ativo).SumAsync(p => p.Preco);

        Assert.Equal(
            "EVALUATE ROW(\"[Value]\", SUMX(FILTER(Produto, Produto[Ativo]), Produto[Preco]))",
            Flat(executor.LastQuery));
        Assert.DoesNotContain("SUM(Produto[Preco])", Flat(executor.LastQuery));
    }

    [Fact]
    public async Task SumAsync_AfterTake_SumsTheWindowAndNotTheTable()
    {
        var executor = new RecordingExecutor();

        await Table(executor).OrderBy(p => p.Id).Take(10).SumAsync(p => p.Preco);

        Assert.Contains("TOPN(10, Produto, Produto[Id], ASC)", Flat(executor.LastQuery));
        Assert.StartsWith("EVALUATE ROW(\"[Value]\", SUMX(TOPN(", Flat(executor.LastQuery));
    }

    [Theory]
    [InlineData("MINX")]
    [InlineData("MAXX")]
    [InlineData("AVERAGEX")]
    public async Task EachAggregate_UsesItsOwnIterator(string iterator)
    {
        var executor = new RecordingExecutor(scalar: 1m);
        DaxTable<Produto> table = Table(executor);

        Task work = iterator switch
        {
            "MINX" => table.MinAsync(p => p.Preco),
            "MAXX" => table.MaxAsync(p => p.Preco),
            _ => table.AverageAsync(p => p.Preco)
        };
        await work;

        Assert.Equal(
            $"EVALUATE ROW(\"[Value]\", {iterator}(Produto, Produto[Preco]))",
            Flat(executor.LastQuery));
    }

    /// <summary>
    /// The selector does not have to be a bare column: any translatable expression becomes the
    /// iterator's second argument, evaluated per row.
    /// </summary>
    [Fact]
    public async Task SumAsync_AcceptsComputedSelector()
    {
        var executor = new RecordingExecutor();

        await Table(executor).SumAsync(p => p.Preco * p.Quantidade);

        Assert.Equal(
            "EVALUATE ROW(\"[Value]\", SUMX(Produto, Produto[Preco] * Produto[Quantidade]))",
            Flat(executor.LastQuery));
    }

    // ---------- BLANK: zero, null or an exception ----------

    [Fact]
    public async Task SumAsync_OverNoRows_IsZero()
    {
        // Sum has a neutral element, and LINQ returns 0 — even though DAX returns BLANK.
        decimal total = await Table(new RecordingExecutor()).SumAsync(p => p.Preco);

        Assert.Equal(0m, total);
    }

    [Fact]
    public async Task SumAsync_OverNoRows_WithNullableSelector_IsNull()
    {
        decimal? total = await Table(new RecordingExecutor()).SumAsync(p => (decimal?)p.Preco);

        Assert.Null(total);
    }

    [Fact]
    public async Task MinAsync_OverNoRows_Throws()
    {
        // Min has no neutral element: returning 0 would claim the lowest price is zero.
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Table(new RecordingExecutor()).MinAsync(p => p.Preco));

        Assert.Equal("Sequence contains no elements.", ex.Message);
    }

    [Fact]
    public async Task MinAsync_OverNoRows_WithNullableSelector_IsNull()
    {
        decimal? menor = await Table(new RecordingExecutor()).MinAsync(p => (decimal?)p.Preco);

        Assert.Null(menor);
    }

    [Fact]
    public async Task MaxAsync_OverNoRows_Throws() =>
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Table(new RecordingExecutor()).MaxAsync(p => p.Preco));

    [Fact]
    public async Task AverageAsync_OverNoRows_Throws()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Table(new RecordingExecutor()).AverageAsync(p => p.Preco));

        Assert.Equal("Sequence contains no elements.", ex.Message);
    }

    [Fact]
    public async Task AverageOrDefaultAsync_OverNoRows_IsNull()
    {
        double? media = await Table(new RecordingExecutor()).AverageOrDefaultAsync(p => p.Preco);

        Assert.Null(media);
    }

    /// <summary>
    /// <c>AverageAsync</c> returns a <see cref="double"/> even over an integer column. Typing the
    /// return as the column would truncate 2.5 to 2 — a wrong number, with no error.
    /// </summary>
    [Fact]
    public async Task AverageAsync_OverIntegerColumn_DoesNotTruncate()
    {
        double media = await Table(new RecordingExecutor(scalar: 2.5)).AverageAsync(p => p.Quantidade);

        Assert.Equal(2.5, media);
    }

    // ---------- scalar conversion ----------

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public async Task Scalar_FromString_UsesInvariantCulture(string culture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            // CurrentCulture is per thread, so this does not escape into other tests.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            // The format of what comes from XMLA belongs to the protocol: "1234.56" is one thousand
            // two hundred in both cultures. Under pt-BR a culture-dependent conversion would read 123456.
            decimal total = await Table(new RecordingExecutor(scalar: "1234.56")).SumAsync(p => p.Preco);

            Assert.Equal(1234.56m, total);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Scalar_OfUnconvertibleType_SaysWhichOperatorFailed()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Table(new RecordingExecutor(scalar: "não é número")).SumAsync(p => p.Preco));

        // The message names the operator and the requested type, in place of Type.Property.
        Assert.Contains("SumAsync<Decimal>", ex.Message);
        Assert.Contains("[Value]", ex.Message);
    }

    // ---------- LongCountAsync ----------

    [Fact]
    public async Task LongCountAsync_ReadsTheSameCountDax()
    {
        var executor = new RecordingExecutor(scalar: 7);

        await Table(executor).LongCountAsync();

        Assert.Equal("EVALUATE ROW(\"[Count]\", COUNTROWS(Produto))", Flat(executor.LastQuery));
    }

    /// <summary>
    /// The operator's reason for existing: a Power BI fact table passes 2 billion rows, and at that
    /// point <c>CountAsync</c> has no right answer to give.
    /// </summary>
    [Fact]
    public async Task LongCountAsync_ReadsCountBeyondIntRange()
    {
        const long rows = 3_000_000_000L;

        long total = await Table(new RecordingExecutor(scalar: rows)).LongCountAsync();

        Assert.Equal(rows, total);
        Assert.True(rows > int.MaxValue);
    }

    [Fact]
    public async Task LongCountAsync_OverBlank_IsZero()
    {
        // COUNTROWS of an empty table is BLANK, and here zero is the right answer.
        long total = await Table(new RecordingExecutor()).LongCountAsync();

        Assert.Equal(0L, total);
    }

    // ---------- SingleAsync ----------

    [Fact]
    public async Task SingleAsync_AsksForTwoRows()
    {
        var executor = new RecordingExecutor(rows: 1);

        await Table(executor).SingleAsync();

        // TOPN(2) is the minimum needed to detect the second row without reading the table.
        Assert.Equal("EVALUATE TOPN(2, Produto)", Flat(executor.LastQuery));
    }

    [Fact]
    public async Task SingleAsync_ReturnsTheOnlyRow()
    {
        Produto produto = await Table(new RecordingExecutor(rows: 1)).SingleAsync();

        Assert.NotNull(produto);
    }

    [Fact]
    public async Task SingleAsync_OverNoRows_Throws()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Table(new RecordingExecutor(rows: 0)).SingleAsync());

        Assert.Equal("Sequence contains no elements.", ex.Message);
    }

    [Fact]
    public async Task SingleAsync_OverTwoRows_Throws()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Table(new RecordingExecutor(rows: 2)).SingleAsync());

        Assert.StartsWith("Sequence contains more than one element.", ex.Message);
        Assert.Contains("FirstAsync", ex.Message);
    }

    [Fact]
    public async Task SingleOrDefaultAsync_OverNoRows_IsNull()
    {
        Produto? produto = await Table(new RecordingExecutor(rows: 0)).SingleOrDefaultAsync();

        Assert.Null(produto);
    }

    /// <summary>
    /// The difference between <c>SingleOrDefault</c> and <c>FirstOrDefault</c>: the "OrDefault"
    /// covers emptiness, not plurality.
    /// </summary>
    [Fact]
    public async Task SingleOrDefaultAsync_OverTwoRows_StillThrows() =>
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Table(new RecordingExecutor(rows: 2)).SingleOrDefaultAsync());

    /// <summary>
    /// The <c>TOPN(2)</c> is one more stage, so after a <c>Take(1)</c> the DAX becomes
    /// <c>TOPN(2, TOPN(1, ...))</c> — the inner one returns a single row and <c>SingleAsync</c> has
    /// no reason to throw.
    /// </summary>
    /// <remarks>
    /// Over the flat definition this required a special case (<c>TopN is 1 ? 1 : 2</c>), because
    /// overwriting the window with 2 would make the query read — and fail over — a row that
    /// <c>Take(1).Single()</c> would never see. The special case went away with the reshape: the
    /// structure stopped needing it.
    /// </remarks>
    [Fact]
    public async Task SingleAsync_AfterTakeOne_NestsTheWindows()
    {
        var executor = new RecordingExecutor(rows: 1);

        await Table(executor).Take(1).SingleAsync();

        Assert.Equal("EVALUATE TOPN(2, TOPN(1, Produto))", Flat(executor.LastQuery));
    }

    [Fact]
    public async Task SingleAsync_AfterLargerTake_NestsTheWindows()
    {
        var executor = new RecordingExecutor(rows: 1);

        await Table(executor).Take(5).SingleAsync();

        Assert.Equal("EVALUATE TOPN(2, TOPN(5, Produto))", Flat(executor.LastQuery));
    }

    // ---------- ToArrayAsync e ToDictionaryAsync ----------

    [Fact]
    public async Task ToArrayAsync_MaterializesEveryRow()
    {
        Produto[] produtos = await Table(new RecordingExecutor(rows: 3)).ToArrayAsync();

        Assert.Equal(3, produtos.Length);
    }

    [Fact]
    public async Task ToDictionaryAsync_IndexesByTheClientSideKey()
    {
        var executor = new RecordingExecutor(rows: 2);
        int i = 0;

        Dictionary<int, Produto> porId = await Table(executor).ToDictionaryAsync(_ => i++);

        Assert.Equal([0, 1], porId.Keys.Order());
        // The selector runs on the client: the DAX is the query's, with no key projection.
        Assert.Equal("EVALUATE Produto", Flat(executor.LastQuery));
    }

    [Fact]
    public async Task ToDictionaryAsync_WithValueSelector_ProjectsTheValue()
    {
        int i = 0;

        Dictionary<int, string> nomes = await Table(new RecordingExecutor(rows: 2))
            .ToDictionaryAsync(_ => i++, p => p.Nome);

        Assert.Equal(2, nomes.Count);
        Assert.All(nomes.Values, nome => Assert.Equal("", nome));
    }

    [Fact]
    public async Task ToDictionaryAsync_WithDuplicateKey_Throws() =>
        await Assert.ThrowsAsync<ArgumentException>(
            () => Table(new RecordingExecutor(rows: 2)).ToDictionaryAsync(_ => 1));

    // ---------- terminals straight on the table ----------

    /// <summary>
    /// The terminals also exist <b>on the table</b>, with no <c>Where</c> first: whoever just wants
    /// to count the table should not have to compose an empty query. They delegate to the query the
    /// table creates, and it is that delegation these tests pin down — if one of them fell into the
    /// wrong overload, the DAX would come out different with no compile error at all.
    /// </summary>
    [Fact]
    public async Task TheTableTerminals_DelegateToTheQueryItCreates()
    {
        var executor = new RecordingExecutor(scalar: 3L, rows: 1);
        DaxTable<Produto> tabela = Table(executor);

        Assert.NotNull(await tabela.FirstAsync());
        Assert.NotNull(await tabela.FirstOrDefaultAsync());
        Assert.Equal(3, await tabela.CountAsync());
        Assert.True(await tabela.AnyAsync());
    }

    [Fact]
    public async Task TheTableTerminals_SeeAnEmptyResultTheSameWayTheQueryDoes()
    {
        var executor = new RecordingExecutor(scalar: 0L, rows: 0);
        DaxTable<Produto> tabela = Table(executor);

        Assert.Null(await tabela.FirstOrDefaultAsync());
        Assert.Equal(0, await tabela.CountAsync());
        Assert.False(await tabela.AnyAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => tabela.FirstAsync());
    }

    // ---------- localized messages ----------

    [Fact]
    public async Task SequenceNotSingle_HasAPortugueseMessage()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PortugueseTable(new RecordingExecutor(rows: 2)).SingleAsync());

        Assert.StartsWith("A sequência contém mais de um elemento.", ex.Message);
    }

    [Fact]
    public async Task SequenceEmpty_FromMinAsync_HasAPortugueseMessage()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PortugueseTable(new RecordingExecutor()).MinAsync(p => p.Preco));

        Assert.Equal("A sequência não contém elementos.", ex.Message);
    }
}
