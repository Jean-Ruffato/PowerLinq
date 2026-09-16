using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Reading a <b>model measure</b>, with the composed filters entering as context.
/// </summary>
/// <remarks>
/// <para>
/// In Power BI the business logic lives in the measures — <c>[Total Vendas]</c> has already been
/// written and reviewed by the BI team, and it is the official source of the report's number.
/// Recomputing it in C# duplicates the rule and risks diverging, which is the worst kind of bug in
/// an indicators context because the number looks plausible.
/// </para>
/// <para>
/// <b>A measure is not a column.</b> A column exists per row and needs an iterator, which is why
/// <c>SumAsync</c> emits <c>SUMX</c>. A measure already is the aggregation: the filters go in as
/// <c>CALCULATE</c>, that is, as the slice to evaluate it over. Wrapping it in an iterator would
/// add it up once per row — valid DAX, wrong number.
/// </para>
/// </remarks>
public sealed class DaxMeasureTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[Id]")] public int Id { get; set; }
        [DaxColumn("Venda[Ano]")] public int Ano { get; set; }
        [DaxColumn("Venda[Categoria]")] public string Categoria { get; set; } = "";
    }

    private sealed class CapturingExecutor(object? scalar = null) : IDaxQueryExecutor
    {
        public string? LastQuery { get; private set; }

        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class => Task.FromResult(new List<T>());

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            return Task.FromResult(scalar);
        }

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    // ---------- the DAX ----------

    [Fact]
    public async Task AMeasureWithoutFilters_NeedsNoCalculate()
    {
        var executor = new CapturingExecutor(1234m);

        _ = await new DaxTable<Venda>(executor).MeasureAsync<decimal>("Total Vendas");

        // A CALCULATE with no filter argument does nothing beyond adding a function to the text.
        Assert.Equal(
            "EVALUATE ROW(\"[Value]\", [Total Vendas])", Flat(executor.LastQuery!));
    }

    [Fact]
    public async Task TheComposedFilters_BecomeTheFilterContext()
    {
        var executor = new CapturingExecutor(1234m);

        _ = await new DaxTable<Venda>(executor)
            .Where(v => v.Ano == 2024)
            .MeasureAsync<decimal>("Total Vendas");

        Assert.Equal(
            "EVALUATE ROW(\"[Value]\", CALCULATE([Total Vendas], FILTER(Venda, Venda[Ano] = 2024)))",
            Flat(executor.LastQuery!));
    }

    /// <summary>
    /// The filter is <b>context</b>, not an iterated source: there is no <c>SUMX</c>,
    /// <c>SUMMARIZECOLUMNS</c> or any iterator around the measure. It is the difference that cannot be got wrong.
    /// </summary>
    [Fact]
    public async Task TheMeasure_IsNeverWrappedInAnIterator()
    {
        var executor = new CapturingExecutor(1234m);

        _ = await new DaxTable<Venda>(executor)
            .Where(v => v.Ano == 2024)
            .MeasureAsync<decimal>("Total Vendas");

        string dax = executor.LastQuery!;

        Assert.DoesNotContain("SUMX", dax, StringComparison.Ordinal);
        Assert.DoesNotContain("AVERAGEX", dax, StringComparison.Ordinal);
        Assert.DoesNotContain("SUMMARIZECOLUMNS", dax, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeveralFilters_AllBecomeFilterArguments()
    {
        var executor = new CapturingExecutor(1234m);

        _ = await new DaxTable<Venda>(executor)
            .Where(v => v.Ano == 2024)
            .Where(v => v.Categoria == "Eletrônicos")
            .MeasureAsync<decimal>("Total Vendas");

        // Consecutive stages become ONE filter with the conjunction, as everywhere else in the library.
        Assert.Equal(
            "EVALUATE ROW(\"[Value]\", CALCULATE([Total Vendas], "
            + "FILTER(Venda, Venda[Ano] = 2024 && Venda[Categoria] = \"Eletrônicos\")))",
            Flat(executor.LastQuery!));
    }

    // ---------- the name ----------

    /// <summary>
    /// With and without brackets resolve the same. In Power BI the measure appears in brackets, and
    /// requiring them to be stripped would produce <c>[[Total Vendas]]</c> — invalid, and only
    /// discovered on the server.
    /// </summary>
    [Theory]
    [InlineData("Total Vendas")]
    [InlineData("[Total Vendas]")]
    [InlineData("  Total Vendas  ")]
    public void TheNameResolvesTheSame_WithOrWithoutBrackets(string escrito)
    {
        Assert.Equal("[Total Vendas]", DaxMeasureName.Resolve(escrito).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyName_IsRefused(string escrito)
    {
        Assert.Throws<ArgumentException>(() => DaxMeasureName.Resolve(escrito));
    }

    // ---------- the result ----------

    [Fact]
    public async Task TheValue_IsConvertedByTheSamePathAsEverythingElse()
    {
        var executor = new CapturingExecutor("1234.56");

        decimal total = await new DaxTable<Venda>(executor).MeasureAsync<decimal>("Total Vendas");

        Assert.Equal(1234.56m, total);
    }

    /// <summary>
    /// <c>BLANK</c> is not zero. A measure over an empty slice returns <c>BLANK</c>, and flattening
    /// it to zero would erase the difference between "summed to zero" and "there was no row" — the
    /// same decision <c>MinAsync</c> and <c>AverageOrDefaultAsync</c> make.
    /// </summary>
    [Fact]
    public async Task ABlankMeasure_ComesBackAsNullAndNotZero()
    {
        var executor = new CapturingExecutor(scalar: null);

        decimal? total = await new DaxTable<Venda>(executor).MeasureAsync<decimal?>("Total Vendas");

        Assert.Null(total);
    }

    // ---------- the refusal ----------

    /// <summary>
    /// After a reshape it is refused. A window does not become a filter-context argument without
    /// changing the meaning, and passing it that way would discard it silently — the measure would
    /// be evaluated over the whole model, returning a plausible and wrong number.
    /// </summary>
    [Theory]
    [InlineData("take")]
    [InlineData("skip")]
    [InlineData("select")]
    public async Task AfterAReshape_TheMeasureIsRefused(string operador)
    {
        var executor = new CapturingExecutor(1m);
        DaxQuery<Venda> consulta = new DaxTable<Venda>(executor).Where(v => v.Ano == 2024);

        DaxQuery<Venda> reformada = operador switch
        {
            "take" => consulta.Take(5),
            "skip" => consulta.OrderBy(v => v.Id).Skip(5),
            _ => consulta
        };

        if (operador == "select")
        {
            await Assert.ThrowsAsync<NotSupportedException>(
                () => consulta.Select(v => new Projetado { Ano = v.Ano })
                              .MeasureAsync<decimal>("Total Vendas"));
            return;
        }

        await Assert.ThrowsAsync<NotSupportedException>(
            () => reformada.MeasureAsync<decimal>("Total Vendas"));
    }

    private sealed class Projetado
    {
        [DaxColumn("Venda[Ano]")] public int Ano { get; set; }
    }
}
