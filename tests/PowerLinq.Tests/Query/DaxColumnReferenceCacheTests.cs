using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Resolving a <c>PropertyInfo</c> to a column reference is cached in a static dictionary. The risk
/// of a global reflection cache is contamination across types, so these tests pin down the
/// isolation: same-named properties on different entities, different tables, and the attribute
/// present or absent.
/// </summary>
public sealed class DaxColumnReferenceCacheTests
{
    // Two entities with a same-named property and NO attribute: the reference depends on the
    // table's name, which is not part of the cache key.
    [DaxTable("Pedido")]
    private sealed class Pedido
    {
        public string Codigo { get; set; } = "";
    }

    [DaxTable("Fatura")]
    private sealed class Fatura
    {
        public string Codigo { get; set; } = "";
    }

    // The same property name, but WITH an attribute pointing at another column: the table's name
    // must be ignored.
    [DaxTable("Contrato")]
    private sealed class Contrato
    {
        [DaxColumn("'Contrato Guarda-Chuva'[Num]")]
        public string Codigo { get; set; } = "";
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
    public void SamePropertyName_InDifferentEntities_ResolvesPerTable()
    {
        string pedido = new DaxTable<Pedido>(new NoopExecutor())
            .Where(x => x.Codigo == "A").ToDaxString();
        string fatura = new DaxTable<Fatura>(new NoopExecutor())
            .Where(x => x.Codigo == "A").ToDaxString();

        Assert.Contains("Pedido[Codigo]", pedido);
        Assert.DoesNotContain("Fatura[Codigo]", pedido);

        Assert.Contains("Fatura[Codigo]", fatura);
        Assert.DoesNotContain("Pedido[Codigo]", fatura);
    }

    [Fact]
    public void SamePropertyName_ReversedOrder_StillResolvesPerTable()
    {
        // Reverses the order of first access: if the cache kept the composed reference instead of
        // the attribute, the second type would inherit the first one's table.
        string fatura = new DaxTable<Fatura>(new NoopExecutor())
            .Where(x => x.Codigo == "B").ToDaxString();
        string pedido = new DaxTable<Pedido>(new NoopExecutor())
            .Where(x => x.Codigo == "B").ToDaxString();

        Assert.Contains("Fatura[Codigo]", fatura);
        Assert.Contains("Pedido[Codigo]", pedido);
    }

    [Fact]
    public void AttributeReference_IgnoresTableName_AndDoesNotLeakToConventionEntities()
    {
        string contrato = new DaxTable<Contrato>(new NoopExecutor())
            .Where(x => x.Codigo == "C").ToDaxString();
        string pedido = new DaxTable<Pedido>(new NoopExecutor())
            .Where(x => x.Codigo == "C").ToDaxString();

        Assert.Contains("'Contrato Guarda-Chuva'[Num]", contrato);
        Assert.DoesNotContain("Contrato[Codigo]", contrato);

        // The convention-based entity is not affected by the other one's attribute.
        Assert.Contains("Pedido[Codigo]", pedido);
        Assert.DoesNotContain("Guarda-Chuva", pedido);
    }

    [Fact]
    public void RepeatedResolution_IsStable()
    {
        // The second and third passes come from the cache; the result must not change.
        var table = new DaxTable<Pedido>(new NoopExecutor());

        string first = table.Where(x => x.Codigo == "D").ToDaxString();
        string second = table.Where(x => x.Codigo == "D").ToDaxString();
        string third = table.Where(x => x.Codigo == "D").ToDaxString();

        Assert.Equal(first, second);
        Assert.Equal(second, third);
        Assert.Contains("Pedido[Codigo]", first);
    }

    [Fact]
    public async Task ConcurrentResolution_IsConsistent()
    {
        // GetOrAdd may run the factory more than once under concurrency; the observed value has to
        // be the same either way.
        var table = new DaxTable<Pedido>(new NoopExecutor());

        string[] results = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ =>
                Task.Run(() => table.Where(x => x.Codigo == "E").ToDaxString())));

        Assert.Single(results.Distinct());
        Assert.Contains("Pedido[Codigo]", results[0]);
    }

    [Fact]
    public void CachedResolution_AlsoAppliesToOrderBy()
    {
        // OrderBy resolves the column through another path (DaxQuery.ResolveColumn), which uses the
        // same cache.
        string dax = new DaxTable<Fatura>(new NoopExecutor())
            .OrderBy(x => x.Codigo).ToDaxString();

        Assert.Contains("ORDER BY Fatura[Codigo] ASC", dax);
    }
}
