using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Context;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

public sealed class DaxContextTests
{
    private sealed class Produto;

    private sealed class Cliente;

    private sealed class Pedido;

    private sealed class Fatura;

    private sealed class TestContext(IDaxQueryExecutor executor, IDaxTableFactory factory)
        : DaxContext(executor, factory)
    {
        public IDaxTable<Produto> Produtos => Set<Produto>();
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

    [Fact]
    public void Context_ReceivesAbstractionsAndCachesTablePerEntity()
    {
        var context = new TestContext(
            new NoopExecutor(),
            new DaxTableFactory(ResourceManagerPowerLinqLocalizer.English));

        IDaxTable<Produto> first = context.Produtos;
        IDaxTable<Produto> second = context.Set<Produto>();

        Assert.Same(first, second);
        Assert.IsAssignableFrom<IDaxTable<Produto>>(first);
    }

    private static TestContext CreateContext() =>
        new(new NoopExecutor(), new DaxTableFactory(ResourceManagerPowerLinqLocalizer.English));

    /// <summary>Rotates across four types, so the tasks contend over different keys.</summary>
    private static object TableFor(TestContext context, int index) => (index % 4) switch
    {
        0 => context.Set<Produto>(),
        1 => context.Set<Cliente>(),
        2 => context.Set<Pedido>(),
        _ => context.Set<Fatura>()
    };

    [Fact]
    public async Task Set_WithDifferentTypesConcurrently_DoesNotCorruptTheDictionary()
    {
        // The pattern that exposes the problem: one context per scope, queries in parallel.
        // On an unsynchronized Dictionary, simultaneous calls with different types can corrupt
        // the internal structure during a resize.
        TestContext context = CreateContext();

        object[] tables = await Task.WhenAll(
            Task.Run(object () => context.Set<Produto>()),
            Task.Run(object () => context.Set<Cliente>()),
            Task.Run(object () => context.Set<Pedido>()),
            Task.Run(object () => context.Set<Fatura>()));

        Assert.Equal(4, tables.Distinct().Count());
    }

    [Fact]
    public async Task Set_WithTheSameTypeConcurrently_ReturnsOneInstanceToEveryCaller()
    {
        // GetOrAdd may run the factory more than once, but only one result is published — and it
        // is that one every caller has to see.
        TestContext context = CreateContext();

        IDaxTable<Produto>[] tables = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => Task.Run(() => context.Set<Produto>())));

        Assert.Single(tables.Distinct());
    }

    [Fact]
    public async Task Set_UnderRepeatedConcurrentPressure_StaysConsistent()
    {
        // Repeats the clash so a rare race gets a chance to show up, instead of depending on the
        // scheduling of a single round.
        for (int round = 0; round < 50; round++)
        {
            TestContext context = CreateContext();

            object[] tables = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(index => Task.Run(() => TableFor(context, index))));

            Assert.Equal(4, tables.Distinct().Count());
        }
    }

    // ---------- compiled queries ----------

    [DaxTable("Produto")]
    private sealed class ProdutoRow
    {
        [DaxColumn("Produto[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("Produto[Preco]")] public decimal Preco { get; set; }
    }

    private sealed class CapturingExecutor : IDaxQueryExecutor
    {
        public string? LastDax { get; private set; }

        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class
        {
            LastDax = daxQuery;
            return Task.FromResult(new List<T>());
        }

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class CompiledContext(IDaxQueryExecutor executor, IDaxTableFactory factory)
        : DaxContext(executor, factory)
    {
        public IDaxTable<ProdutoRow> Produtos => Set<ProdutoRow>();
    }

    [Fact]
    public async Task FromCompiledAsync_BindsTheSingleParameterIntoTheExecutedDax()
    {
        var executor = new CapturingExecutor();
        var context = new CompiledContext(
            executor, new DaxTableFactory(ResourceManagerPowerLinqLocalizer.English));

        DaxCompiledQuery<ProdutoRow, string> porCategoria =
            DaxCompiledQuery.Create<ProdutoRow, string>(
                (tabela, categoria) => tabela.Where(p => p.Categoria == categoria));

        await context.FromCompiledAsync(porCategoria, "Eletrônicos");

        Assert.Contains("Produto[Categoria] = \"Eletrônicos\"", executor.LastDax);
    }

    [Fact]
    public async Task FromCompiledAsync_BindsBothParametersByPosition()
    {
        var executor = new CapturingExecutor();
        var context = new CompiledContext(
            executor, new DaxTableFactory(ResourceManagerPowerLinqLocalizer.English));

        DaxCompiledQuery<ProdutoRow, string, decimal> consulta =
            DaxCompiledQuery.Create<ProdutoRow, string, decimal>(
                (tabela, categoria, minimo) => tabela
                    .Where(p => p.Categoria == categoria)
                    .Where(p => p.Preco > minimo));

        await context.FromCompiledAsync(consulta, "Móveis", 99.5m);

        Assert.Contains("Produto[Categoria] = \"Móveis\"", executor.LastDax);
        Assert.Contains("Produto[Preco] > 99.5", executor.LastDax);
    }

    [Fact]
    public async Task FromCompiledAsync_RejectsANullQuery()
    {
        var context = new CompiledContext(
            new NoopExecutor(), new DaxTableFactory(ResourceManagerPowerLinqLocalizer.English));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => context.FromCompiledAsync<ProdutoRow, string>(null!, "x"));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => context.FromCompiledAsync<ProdutoRow, string, decimal>(null!, "x", 1m));
    }

    [Fact]
    public void Context_DoesNotDisposeTheInjectedExecutor()
    {
        // Decision recorded in DaxContext's <remarks>: the executor is received, not created, so
        // its lifetime belongs to the container. An earlier version implemented IAsyncDisposable,
        // and that was removed when DI was adopted (a3764ec).
        Assert.False(
            typeof(DaxContext).IsAssignableTo(typeof(IAsyncDisposable)),
            "DaxContext não deve descartar dependência injetada");
        Assert.False(
            typeof(DaxContext).IsAssignableTo(typeof(IDisposable)),
            "DaxContext não deve descartar dependência injetada");
    }
}
