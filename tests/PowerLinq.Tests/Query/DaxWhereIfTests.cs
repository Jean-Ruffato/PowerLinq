using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

public class DaxWhereIfTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[Exportador]")] public string Exportador { get; set; } = "";
        [DaxColumn("Venda[Periodo]")] public string Periodo { get; set; } = "";
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

    [Fact]
    public void WhereIf_TrueCondition_AppliesFilter()
    {
        string dax = Table().WhereIf(true, x => x.Exportador == "ACME").ToDaxString();

        Assert.Contains("FILTER(", dax);
        Assert.Contains("Venda[Exportador] = \"ACME\"", dax);
    }

    [Fact]
    public void WhereIf_FalseCondition_OmitsFilter()
    {
        string dax = Table().WhereIf(false, x => x.Exportador == "ACME").ToDaxString();

        Assert.DoesNotContain("FILTER(", dax);
        Assert.DoesNotContain("Exportador", dax);
    }

    [Theory]
    [InlineData("ACME")]
    [InlineData("  ACME  ")]
    public void WhereIf_StringWithValue_AppliesFilter(string value)
    {
        string dax = Table().WhereIf(value, x => x.Exportador == value).ToDaxString();

        Assert.Contains("Venda[Exportador] =", dax);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WhereIf_StringWithoutValue_OmitsFilter(string? value)
    {
        string dax = Table().WhereIf(value, x => x.Exportador == value).ToDaxString();

        Assert.DoesNotContain("FILTER(", dax);
    }

    [Fact]
    public void WhereIf_MixedConditions_KeepsOnlyApplied()
    {
        // Only the period goes in; the exporter (a false condition) is ignored.
        string dax = Table()
            .WhereIf(false, x => x.Exportador == "ACME")
            .WhereIf("2024-01", x => x.Periodo == "2024-01")
            .ToDaxString();

        Assert.DoesNotContain("Exportador", dax);
        Assert.Contains("Venda[Periodo] = \"2024-01\"", dax);
    }

    [Fact]
    public void WhereIf_ComposesWithWhere_CombinesWithAnd()
    {
        string dax = Table()
            .Where(x => x.Periodo == "2024-01")
            .WhereIf(true, x => x.Exportador == "ACME")
            .ToDaxString();

        Assert.Contains("&&", dax);
        Assert.Contains("Venda[Periodo] = \"2024-01\"", dax);
        Assert.Contains("Venda[Exportador] = \"ACME\"", dax);
    }
}
