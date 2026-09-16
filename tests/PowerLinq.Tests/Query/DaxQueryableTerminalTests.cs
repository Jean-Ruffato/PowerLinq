using System.Runtime.CompilerServices;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Linq;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// The <c>IQueryable</c> execution terminals — all the ones the fluent API already had.
/// </summary>
/// <remarks>
/// <para>
/// The operators-per-class matrix had <c>ToListAsync</c> reimplemented in five places. A new
/// surface only makes that count worse if it rewrites the terminals; here each one converts the
/// query back into a <c>DaxQuery</c> and calls what already existed. What these tests verify is
/// exactly that: the DAX reaching the executor is the same, and no terminal was left off the
/// bridge.
/// </para>
/// <para>
/// <c>AsAsyncEnumerable</c> and <c>ToPagedListAsync</c> require an extra capability from the
/// executor, and the refusal when it is missing is the same as the fluent API's — the separate
/// interface is the mechanism, not an exception of this layer.
/// </para>
/// </remarks>
public sealed class DaxQueryableTerminalTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("Produto[Preco]")] public decimal Preco { get; set; }
        [DaxColumn("Produto[Ativo]")] public bool Ativo { get; set; }
    }

    /// <summary>An executor with all three capabilities, to cover the terminals in a single file.</summary>
    private sealed class FullExecutor(int rows = 0, object? scalar = null)
        : IDaxQueryExecutor, IDaxStreamingQueryExecutor, IDaxRawQueryExecutor
    {
        public string? LastQuery { get; private set; }

        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class
        {
            LastQuery = daxQuery;

            return Task.FromResult(Enumerable.Range(0, rows)
                .Select(id => (T)(object)new Produto { Id = id, Nome = $"Item {id}" })
                .ToList());
        }

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            return Task.FromResult<object?>(scalar ?? rows);
        }

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            return Task.FromResult(rows);
        }

        public async IAsyncEnumerable<T> ExecuteStreamAsync<T>(
            string daxQuery,
            [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class
        {
            LastQuery = daxQuery;

            for (int id = 0; id < rows; id++)
            {
                await Task.Yield();
                yield return (T)(object)new Produto { Id = id };
            }
        }

        public Task<DaxResult> ExecuteRowsAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;

            DaxRow[] page = [.. Enumerable.Range(0, rows).Select(id => new DaxRow(
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Produto[Id]"] = id,
                    ["Produto[Nome]"] = $"Item {id}",
                    ["[__pl_total]"] = 87L
                }))];

            return Task.FromResult(new DaxResult(["Produto[Id]", "Produto[Nome]", "[__pl_total]"], page));
        }
    }

    private static IQueryable<Produto> Queryable(IDaxQueryExecutor executor) =>
        new DaxTable<Produto>(executor).AsQueryable();

    /// <summary>The DAX on a single line, so the assertion does not depend on the writer's indentation.</summary>
    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToArrayAsync_MaterializesEveryRow()
    {
        var executor = new FullExecutor(rows: 3);

        Assert.Equal(3, (await Queryable(executor).ToArrayAsync()).Length);
    }

    [Fact]
    public async Task ToDictionaryAsync_IndexesInTheClient()
    {
        var executor = new FullExecutor(rows: 2);

        Dictionary<int, Produto> porId = await Queryable(executor).ToDictionaryAsync(p => p.Id);

        Assert.Equal([0, 1], porId.Keys.Order());
    }

    [Fact]
    public async Task LongCountAsync_ReadsTheCountAsLong()
    {
        var executor = new FullExecutor(scalar: 3_000_000_000L);

        Assert.Equal(3_000_000_000L, await Queryable(executor).LongCountAsync());
    }

    [Fact]
    public async Task CountAsync_WithPredicate_FiltersBeforeCounting()
    {
        var executor = new FullExecutor(rows: 4);

        Assert.Equal(4, await Queryable(executor).CountAsync(p => p.Ativo));
        Assert.Contains("Produto[Ativo]", executor.LastQuery!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleAsync_AsksForTwoRowsToDetectTheSecond()
    {
        var executor = new FullExecutor(rows: 1);

        Assert.Equal(0, (await Queryable(executor).SingleAsync()).Id);
        Assert.Contains("TOPN(2,", Flat(executor.LastQuery!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleOrDefaultAsync_OnAnEmptyResult_IsNull()
    {
        Assert.Null(await Queryable(new FullExecutor()).SingleOrDefaultAsync());
    }

    /// <summary>
    /// The <b>predicate-free</b> forms of the same terminals go over the same bridge. They are a
    /// separate overload, so covering only the predicate one would leave half the bridge untested.
    /// </summary>
    [Fact]
    public async Task TheParameterlessTerminals_CrossTheSameBridge()
    {
        var executor = new FullExecutor(rows: 1, scalar: 1L);

        Assert.Equal(0, (await Queryable(executor).FirstAsync()).Id);
        Assert.NotNull(await Queryable(executor).FirstOrDefaultAsync());
        Assert.True(await Queryable(executor).AnyAsync());
    }

    [Fact]
    public async Task TheParameterlessTerminals_OnAnEmptyResult()
    {
        var executor = new FullExecutor(rows: 0, scalar: 0L);

        Assert.Null(await Queryable(executor).FirstOrDefaultAsync());
        Assert.False(await Queryable(executor).AnyAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Queryable(executor).FirstAsync());
    }

    [Fact]
    public async Task FirstAsync_WithPredicate_AppliesItOnTheServer()
    {
        var executor = new FullExecutor(rows: 1);

        await Queryable(executor).FirstAsync(p => p.Ativo);

        Assert.Contains("FILTER(Produto, Produto[Ativo])", Flat(executor.LastQuery!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllAsync_CountsTheRowsThatFailThePredicate()
    {
        var executor = new FullExecutor(rows: 0);

        Assert.True(await Queryable(executor).AllAsync(p => p.Ativo));
        Assert.Contains("NOT", executor.LastQuery!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MinAndMax_IterateTheColumnOnTheServer()
    {
        var executor = new FullExecutor(scalar: 12m);

        Assert.Equal(12m, await Queryable(executor).MinAsync(p => p.Preco));
        Assert.Contains("MINX", executor.LastQuery!, StringComparison.Ordinal);

        Assert.Equal(12m, await Queryable(executor).MaxAsync(p => p.Preco));
        Assert.Contains("MAXX", executor.LastQuery!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AverageAsync_ReturnsDoubleRegardlessOfTheColumnType()
    {
        var executor = new FullExecutor(scalar: 2.5d);

        Assert.Equal(2.5d, await Queryable(executor).AverageAsync(p => p.Id));
        Assert.Contains("AVERAGEX", executor.LastQuery!, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A measure is not a column: the composed <c>Where</c> goes in as filter context, and the
    /// measure already is the aggregation — with no iterator around it.
    /// </remarks>
    [Fact]
    public async Task MeasureAsync_EvaluatesTheModelMeasureUnderTheComposedFilter()
    {
        var executor = new FullExecutor(scalar: 1234m);

        decimal total = await Queryable(executor)
            .Where(p => p.Ativo)
            .MeasureAsync<Produto, decimal>("Total Vendas");

        Assert.Equal(1234m, total);
        Assert.Contains("[Total Vendas]", executor.LastQuery!, StringComparison.Ordinal);
        Assert.Contains("CALCULATE", executor.LastQuery!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsAsyncEnumerable_DeliversRowsAsTheyArrive()
    {
        var executor = new FullExecutor(rows: 3);
        var lidos = new List<int>();

        await foreach (Produto produto in Queryable(executor).Where(p => p.Ativo).AsAsyncEnumerable())
            lidos.Add(produto.Id);

        Assert.Equal([0, 1, 2], lidos);
        Assert.Contains("FILTER(Produto, Produto[Ativo])", Flat(executor.LastQuery!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToPagedListAsync_BringsThePageAndTheTotalInOneQuery()
    {
        var executor = new FullExecutor(rows: 2);

        DaxPage<Produto> pagina = await Queryable(executor)
            .OrderBy(p => p.Id)
            .Skip(20)
            .Take(2)
            .ToPagedListAsync();

        Assert.Equal(2, pagina.Items.Count);
        Assert.Equal(87L, pagina.Total);
    }
}
