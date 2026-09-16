using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PowerLinq.ConnectionPool.Executors;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.ConnectionPool.Options;
using PowerLinq.DaxConverter.Context;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.Extensions.DependencyInjection;

namespace PowerLinq.Tests.ConnectionPool;

/// <summary>
/// An explicit query target.
/// </summary>
/// <remarks>
/// The executor froze the connection string in its constructor, from appsettings: one workspace and
/// one dataset for the whole process. That covers one application against one model, but not the
/// multi-tenant case — the same dataset published in one workspace per customer, with the target
/// only known partway through the request. The pool already took the string per call and grouped
/// by it; the knot was in the executor.
/// </remarks>
public sealed class DaxTargetTests
{
    /// <summary>A fake pool that records each lease's key — it is the key that groups connections.</summary>
    private sealed class RecordingPool : IXmlaConnectionPool
    {
        public List<string> Rentals { get; } = [];

        public Task<IPooledXmlaConnection> RentAsync(
            string connectionString, CancellationToken cancellationToken = default)
        {
            Rentals.Add(connectionString);
            return Task.FromResult<IPooledXmlaConnection>(new Lease());
        }

        private sealed class Lease : IPooledXmlaConnection
        {
            public IXmlaConnection Connection { get; } = new FakeXmlaConnection("lease");

            public void MarkBroken() { }

            public void Dispose() { }
        }
    }

    private static IOptions<PowerBiOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new PowerBiOptions
        {
            XmlaEndpoint = "powerbi://api.powerbi.com/v1.0/myorg/Padrao",
            Dataset = "ModeloPadrao"
        });

    private static PooledXmlaQueryExecutorFactory Factory(RecordingPool pool) =>
        new(pool, Options(), ResourceManagerPowerLinqLocalizer.English);

    private sealed class Linha
    {
        public int Valor { get; set; }
    }

    // ---------- different targets in the same application ----------

    [Fact]
    public async Task TwoTargets_ProduceTwoDistinctPoolKeys()
    {
        var pool = new RecordingPool();
        PooledXmlaQueryExecutorFactory factory = Factory(pool);

        await factory
            .Create(new DaxTarget("powerbi://api.powerbi.com/v1.0/myorg/ClienteA", "Modelo"))
            .ExecuteAsync<Linha>("EVALUATE Tabela");

        await factory
            .Create(new DaxTarget("powerbi://api.powerbi.com/v1.0/myorg/ClienteB", "Modelo"))
            .ExecuteAsync<Linha>("EVALUATE Tabela");

        Assert.Equal(2, pool.Rentals.Count);
        Assert.Equal(2, pool.Rentals.Distinct().Count());
        Assert.Contains("Data Source=powerbi://api.powerbi.com/v1.0/myorg/ClienteA;Catalog=Modelo;", pool.Rentals);
        Assert.Contains("Data Source=powerbi://api.powerbi.com/v1.0/myorg/ClienteB;Catalog=Modelo;", pool.Rentals);
    }

    [Fact]
    public async Task TheSameTarget_KeepsASinglePoolKey()
    {
        // What guarantees connections stay grouped by target, and not one per query.
        var pool = new RecordingPool();
        PooledXmlaQueryExecutorFactory factory = Factory(pool);
        var target = new DaxTarget("powerbi://api.powerbi.com/v1.0/myorg/ClienteA", "Modelo");

        for (int i = 0; i < 5; i++)
            await factory.Create(target).ExecuteAsync<Linha>("EVALUATE Tabela");

        Assert.Equal(5, pool.Rentals.Count);
        Assert.Single(pool.Rentals.Distinct());
    }

    [Fact]
    public async Task DifferentDatasetsInTheSameWorkspace_AreDifferentKeys()
    {
        var pool = new RecordingPool();
        PooledXmlaQueryExecutorFactory factory = Factory(pool);
        const string workspace = "powerbi://api.powerbi.com/v1.0/myorg/Unico";

        await factory.Create(new DaxTarget(workspace, "ModeloA")).ExecuteAsync<Linha>("EVALUATE T");
        await factory.Create(new DaxTarget(workspace, "ModeloB")).ExecuteAsync<Linha>("EVALUATE T");

        Assert.Equal(2, pool.Rentals.Distinct().Count());
    }

    // ---------- no regression: a single target ----------

    [Fact]
    public async Task WithoutATarget_TheExecutorStillUsesTheConfiguredDestination()
    {
        var pool = new RecordingPool();
        var executor = new PooledXmlaQueryExecutor(pool, Options());

        await executor.ExecuteAsync<Linha>("EVALUATE Tabela");

        Assert.Equal(
            "Data Source=powerbi://api.powerbi.com/v1.0/myorg/Padrao;Catalog=ModeloPadrao;",
            Assert.Single(pool.Rentals));
    }

    [Fact]
    public void BuildXmlaConnectionString_WithoutTarget_IsUnchanged()
    {
        var options = new PowerBiOptions { XmlaEndpoint = "endpoint", Dataset = "modelo" };

        Assert.Equal("Data Source=endpoint;Catalog=modelo;", options.BuildXmlaConnectionString());
    }

    [Fact]
    public void BuildXmlaConnectionString_WithTarget_OverridesBothFields()
    {
        var options = new PowerBiOptions { XmlaEndpoint = "endpoint", Dataset = "modelo" };

        Assert.Equal(
            "Data Source=outro;Catalog=outroModelo;",
            options.BuildXmlaConnectionString(new DaxTarget("outro", "outroModelo")));
    }

    // ---------- an unresolved target ----------

    [Theory]
    [InlineData("", "Modelo")]
    [InlineData("   ", "Modelo")]
    [InlineData("workspace", "")]
    [InlineData("", "")]
    public void AnIncompleteTarget_IsRefusedNamingBothFields(string workspace, string dataset)
    {
        PooledXmlaQueryExecutorFactory factory = Factory(new RecordingPool());

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => factory.Create(new DaxTarget(workspace, dataset)));

        Assert.Contains("workspace=", ex.Message);
        Assert.Contains("dataset=", ex.Message);
    }

    [Fact]
    public void AnIncompleteTarget_FailsAtCreation_NotAtExecution()
    {
        // Failing at creation is the point: the stack trace points at where the tenant was
        // resolved, not inside a query that had no way to know the target was invalid.
        var pool = new RecordingPool();
        PooledXmlaQueryExecutorFactory factory = Factory(pool);

        Assert.Throws<ArgumentException>(() => factory.Create(new DaxTarget("", "Modelo")));
        Assert.Empty(pool.Rentals);
    }

    [Fact]
    public void TargetMessage_IsLocalized()
    {
        IPowerLinqLocalizer portuguese = new ResourceManagerPowerLinqLocalizer("pt-BR");

        Assert.Contains(
            "destino da consulta está incompleto",
            portuguese.Format("TargetIncomplete", "", "Modelo"));
    }

    [Fact]
    public void ANullTarget_IsRefused()
    {
        PooledXmlaQueryExecutorFactory factory = Factory(new RecordingPool());

        Assert.Throws<ArgumentNullException>(() => factory.Create(null!));
    }

    // ---------- context factory ----------

    private sealed class TenantContext(IDaxQueryExecutor executor, IDaxTableFactory tableFactory)
        : DaxContext(executor, tableFactory)
    {
        public IDaxTable<Linha> Linhas => Set<Linha>();
    }

    private static IServiceProvider Provider(RecordingPool pool)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PowerBi:XmlaEndpoint"] = "powerbi://api.powerbi.com/v1.0/myorg/Padrao",
                ["PowerBi:Dataset"] = "ModeloPadrao"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddXmlaConnectionPool(configuration);

        // The single-target executor is registered by the application, as the README shows — not by
        // AddXmlaConnectionPool. Without it the container's context does not resolve, which is the
        // current contract and does not change with the explicit target.
        services.AddScoped<IDaxQueryExecutor, PooledXmlaQueryExecutor>();
        services.AddDaxContext<TenantContext>(configuration);

        // Replaces only the pool, to observe the lease keys without touching the rest of the wiring.
        services.AddSingleton<IXmlaConnectionPool>(pool);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task TheContextFactory_BindsTheContextToTheTarget()
    {
        var pool = new RecordingPool();
        IDaxContextFactory contexts = Provider(pool).GetRequiredService<IDaxContextFactory>();

        TenantContext clienteA = contexts.Create<TenantContext>(
            new DaxTarget("powerbi://api.powerbi.com/v1.0/myorg/ClienteA", "Modelo"));
        TenantContext clienteB = contexts.Create<TenantContext>(
            new DaxTarget("powerbi://api.powerbi.com/v1.0/myorg/ClienteB", "Modelo"));

        await clienteA.Linhas.ToListAsync();
        await clienteB.Linhas.ToListAsync();

        Assert.Equal(2, pool.Rentals.Distinct().Count());
        Assert.Contains("Catalog=Modelo;", pool.Rentals[0]);
        Assert.Contains("ClienteA", pool.Rentals[0]);
        Assert.Contains("ClienteB", pool.Rentals[1]);
    }

    [Fact]
    public void TheContextFactory_RefusesAnIncompleteTarget()
    {
        IDaxContextFactory contexts = Provider(new RecordingPool()).GetRequiredService<IDaxContextFactory>();

        Assert.Throws<ArgumentException>(
            () => contexts.Create<TenantContext>(new DaxTarget("", "Modelo")));
    }

    [Fact]
    public void TheContextFromTheContainer_IsUnaffected()
    {
        // The single-target application does not change: the context resolved from the container
        // keeps using the registered executor, with the configured target.
        IServiceProvider provider = Provider(new RecordingPool());

        Assert.NotNull(provider.GetService<TenantContext>());
    }

    // ---------- wiring ----------

    [Fact]
    public void TheExecutorFactory_IsRegisteredByAddXmlaConnectionPool()
    {
        IServiceProvider provider = Provider(new RecordingPool());

        Assert.IsType<PooledXmlaQueryExecutorFactory>(
            provider.GetRequiredService<IDaxQueryExecutorFactory>());
    }

    [Fact]
    public void TheContextFactory_IsRegisteredByAddDaxContext()
    {
        IServiceProvider provider = Provider(new RecordingPool());

        Assert.IsType<DaxContextFactory>(provider.GetRequiredService<IDaxContextFactory>());
    }

    [Fact]
    public void TargetEquality_IsByValue()
    {
        // The pool groups by the string derived from the target; equality by value is what makes
        // "the same target" a usable concept for anyone caching contexts per tenant.
        Assert.Equal(new DaxTarget("w", "d"), new DaxTarget("w", "d"));
        Assert.NotEqual(new DaxTarget("w", "d"), new DaxTarget("w", "outro"));
    }
}
