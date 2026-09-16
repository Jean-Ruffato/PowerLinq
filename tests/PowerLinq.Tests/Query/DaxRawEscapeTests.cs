using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Context;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;

namespace PowerLinq.Tests.Query;

/// <summary>
/// The escape hatch for hand-written DAX.
/// </summary>
/// <remarks>
/// <para>
/// A translation library eventually needs an escape, and the question is not whether it exists —
/// it is whether it is <b>declared</b> or reinvented. Before this, reading a measure went through
/// <c>ExecuteCountAsync</c>, a method whose name says "count": the detour existed, unnamed and
/// undocumented, and nobody reading the call would notice.
/// </para>
/// <para>
/// What these tests pin down is the door's contract: it sends the text <b>as it came</b>, and
/// materializes through the same machinery as everything else.
/// </para>
/// </remarks>
public sealed class DaxRawEscapeTests
{
    private sealed class Linha
    {
        [DaxColumn("[nome]")] public string Nome { get; set; } = "";
        [DaxColumn("[valor]")] public decimal Valor { get; set; }
    }

    private sealed class Contexto(IDaxQueryExecutor executor, IDaxTableFactory factory)
        : DaxContext(executor, factory);

    private sealed class CapturingExecutor(object? scalar = null) : IDaxQueryExecutor
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
            return Task.FromResult(scalar);
        }

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private static Contexto Context(CapturingExecutor executor) =>
        new(executor, new DaxTableFactory(ResourceManagerPowerLinqLocalizer.English));

    /// <summary>
    /// The text goes out <b>as it came</b>. That is the point of the door: it does not interpret,
    /// normalize or rewrite — if it rewrote, it would not serve what the translation cannot express.
    /// </summary>
    [Fact]
    public async Task TheQuery_IsSentVerbatim()
    {
        var executor = new CapturingExecutor();

        // With whitespace at the edges and a line break in the middle, on purpose: an earlier
        // version of this test used already-clean text, and so did not tell "sends as it came"
        // from "sends normalized" — the mutation that added a Trim() survived it.
        const string dax = "\n  EVALUATE\n    SUMMARIZECOLUMNS('DIM'[X], \"v\", [Minha Medida])\n  ";

        _ = await Context(executor).FromRawDaxAsync<Linha>(dax);

        Assert.Equal(dax, executor.LastQuery);
    }

    /// <summary>
    /// Materialization is the same as everywhere else: the same attributes, the same conversion. A
    /// type table of its own here would diverge from the other the first time one gained a type.
    /// </summary>
    [Fact]
    public async Task TheScalar_IsConvertedByTheSamePathAsEverythingElse()
    {
        var executor = new CapturingExecutor("1234.56");

        decimal valor = await Context(executor)
            .FromRawDaxScalarAsync<decimal>("EVALUATE ROW(\"x\", [Total])");

        Assert.Equal(1234.56m, valor);
    }

    /// <summary>
    /// <c>BLANK</c> comes back as <see langword="null"/>, not as zero — the same decision as
    /// <c>MinAsync</c> and <c>MeasureAsync</c>.
    /// </summary>
    [Fact]
    public async Task ABlankScalar_ComesBackAsNullAndNotZero()
    {
        var executor = new CapturingExecutor(scalar: null);

        decimal? valor = await Context(executor)
            .FromRawDaxScalarAsync<decimal?>("EVALUATE ROW(\"x\", [Total])");

        Assert.Null(valor);
    }

    /// <summary>
    /// The only check that belongs. Validating more would mean reimplementing the server's DAX
    /// parser, and the reason this door exists is to accept what the translation cannot express.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyQuery_IsRefused(string dax)
    {
        var executor = new CapturingExecutor();

        await Assert.ThrowsAsync<ArgumentException>(
            () => Context(executor).FromRawDaxAsync<Linha>(dax));

        await Assert.ThrowsAsync<ArgumentException>(
            () => Context(executor).FromRawDaxScalarAsync<decimal>(dax));
    }

    /// <summary>
    /// Nothing is sent when the query is empty: the refusal happens before the executor.
    /// </summary>
    [Fact]
    public async Task AnEmptyQuery_NeverReachesTheExecutor()
    {
        var executor = new CapturingExecutor();

        await Assert.ThrowsAsync<ArgumentException>(
            () => Context(executor).FromRawDaxAsync<Linha>("  "));

        Assert.Null(executor.LastQuery);
    }
}
