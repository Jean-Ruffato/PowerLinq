using System.Collections.Frozen;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Mapping;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Mapping;

/// <summary>
/// The attribute's reference matches the name the server returns, in <b>both</b> spellings.
/// </summary>
/// <remarks>
/// <para>
/// The <c>[DaxColumn]</c> string serves two purposes: it becomes the reference text in the
/// generated DAX <b>and</b> it is the key used to find the column in the result. ADOMD returns the
/// column name with the table <b>unquoted</b> — <c>T[C]</c> — while DAX's convention, which the
/// library itself emits in <c>DaxIdentifier.Quote</c>, is <c>'T'[C]</c>.
/// </para>
/// <para>
/// Registering only the literal made whoever wrote it quoted <b>generate correct DAX and
/// materialize everything with the type's default</b>: no error, no wrong query, and an empty
/// screen. In an option list that showed up as "no filter appeared"; in a metric, zero.
/// </para>
/// <para>
/// The workaround was writing it unquoted, but that only holds while the table name is a simple
/// identifier: a table with a space <b>needs</b> the quotes in DAX, and then there was no spelling
/// that served both sides.
/// </para>
/// </remarks>
public sealed class QuotedReferenceMappingTests
{
    [DaxTable("FAT_REALIZADO")]
    private sealed class ComAspas
    {
        [DaxColumn("'FAT_REALIZADO'[SK_PERIODO]")] public int PeriodoSk { get; set; }
        [DaxColumn("'FAT_REALIZADO'[PERIODO]")] public string? Periodo { get; set; }
        [DaxColumn("[realized_rows]")] public long? Linhas { get; set; }
    }

    [DaxTable("Ordem de Venda")]
    private sealed class ComEspaco
    {
        [DaxColumn("'Ordem de Venda'[Qtd]")] public int Qtd { get; set; }
    }

    private static DaxRow Row(params (string Column, object? Value)[] cells) =>
        new(cells.ToDictionary(cell => cell.Column, cell => cell.Value));

    private static T Map<T>(DaxRow row) where T : class =>
        EntityMapper.MapRow<T>(row, EntityMapper.GetColumnMappings(typeof(T)));

    /// <summary>
    /// The real case, with the keys <b>exactly</b> as ADOMD returns them: the table unquoted.
    /// </summary>
    [Fact]
    public void AQuotedAttribute_MatchesTheUnquotedNameTheServerReturns()
    {
        ComAspas linha = Map<ComAspas>(Row(
            ("FAT_REALIZADO[SK_PERIODO]", 270059),
            ("FAT_REALIZADO[PERIODO]", "2025-03"),
            ("[realized_rows]", 12L)));

        Assert.Equal(270059, linha.PeriodoSk);
        Assert.Equal("2025-03", linha.Periodo);
        Assert.Equal(12L, linha.Linhas);
    }

    /// <summary>And it still matches the quoted spelling, which is what the query itself emits.</summary>
    [Fact]
    public void AQuotedAttribute_StillMatchesTheQuotedName()
    {
        ComAspas linha = Map<ComAspas>(Row(
            ("'FAT_REALIZADO'[SK_PERIODO]", 270059),
            ("'FAT_REALIZADO'[PERIODO]", "2025-03")));

        Assert.Equal(270059, linha.PeriodoSk);
        Assert.Equal("2025-03", linha.Periodo);
    }

    /// <summary>
    /// The inverse: an <b>unquoted</b> attribute on a name that needs them matches the quoted form.
    /// </summary>
    /// <remarks>
    /// It only holds when the name requires quotes. <c>DaxIdentifier.Quote</c> does not quote a
    /// simple identifier, so for <c>FAT_REALIZADO</c> both spellings <b>coincide</b> and there is no
    /// second key to register — an earlier version of this test expected
    /// <c>'FAT_REALIZADO'[PERIODO]</c> and failed because of it.
    /// </remarks>
    [Fact]
    public void AnUnquotedAttribute_MatchesTheQuotedNameWhenTheNameNeedsQuotes()
    {
        FrozenDictionary<string, DaxColumnMapping> mappings =
            EntityMapper.GetColumnMappings(typeof(SemAspas));

        Assert.True(mappings.ContainsKey("Ordem de Venda[Qtd]"));
        Assert.True(mappings.ContainsKey("'Ordem de Venda'[Qtd]"));
    }

    [DaxTable("Ordem de Venda")]
    private sealed class SemAspas
    {
        [DaxColumn("Ordem de Venda[Qtd]")] public int Qtd { get; set; }
    }

    /// <summary>
    /// A table with a space is the case that <b>had no workaround</b>: DAX requires the quotes, and
    /// the result comes back without them.
    /// </summary>
    [Fact]
    public void ATableNameWithASpace_MatchesBothWays()
    {
        Assert.Equal(7, Map<ComEspaco>(Row(("Ordem de Venda[Qtd]", 7))).Qtd);
        Assert.Equal(7, Map<ComEspaco>(Row(("'Ordem de Venda'[Qtd]", 7))).Qtd);
    }

    /// <summary>
    /// An extension column has no table in its reference, so there is no second spelling — and
    /// nothing changes for it. That is why the bug slipped through: <c>[alias]</c> always matched.
    /// </summary>
    [Fact]
    public void AnExtensionColumn_HasNoSecondSpelling()
    {
        FrozenDictionary<string, DaxColumnMapping> mappings =
            EntityMapper.GetColumnMappings(typeof(ComAspas));

        Assert.True(mappings.ContainsKey("[realized_rows]"));
    }

    /// <summary>
    /// The query still emits the spelling the user wrote — the generated DAX does not change.
    /// </summary>
    [Fact]
    public void TheGeneratedDax_KeepsTheSpellingTheUserWrote()
    {
        string dax = new DaxTable<ComAspas>(new NoopExecutor())
            .Where(r => r.PeriodoSk == 1)
            .ToDaxString();

        Assert.Contains("'FAT_REALIZADO'[SK_PERIODO] = 1", dax, StringComparison.Ordinal);
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
}
