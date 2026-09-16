using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Text slicing and text-to-number conversion <b>on the server</b>.
/// </summary>
/// <remarks>
/// <para>
/// The motivating case: a period stored as <c>MM-yyyy</c> text. Sorting that column on the server
/// sorts the <b>text</b>, and <c>01-2026</c> comes before <c>12-2024</c>. Without <c>MID</c> plus
/// <c>VALUE</c> there is no way to build the <c>year * 100 + month</c> key on the server, and the
/// client has to fetch every period in order to sort.
/// </para>
/// <para>
/// <c>int.Parse("1.5")</c> over a captured variable <b>already</b> worked, because it does not
/// depend on the lambda's parameter and is evaluated during translation. What was missing was over a column.
/// </para>
/// </remarks>
public sealed class DaxTextToNumberTests
{
    [DaxTable("Fato")]
    private sealed class Fato
    {
        [DaxColumn("Fato[Periodo]")] public string Periodo { get; set; } = "";
        [DaxColumn("Fato[Valor]")] public decimal Valor { get; set; }
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

    private static DaxTable<Fato> Table() => new(new NoopExecutor());

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    // ---------- the slice ----------

    /// <summary>
    /// <c>MID</c> is 1-based and <c>Substring</c> is 0-based. Without the adjustment the slice
    /// comes out one character off — a silent error, not a failure.
    /// </summary>
    [Fact]
    public void SubstringWithTwoArguments_BecomesMidOneBased()
    {
        string dax = Flat(Table().Where(f => f.Periodo.Substring(0, 2) == "12").ToDaxString());

        Assert.Contains("MID(Fato[Periodo], 1, 2)", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Substring(i)</c> runs to the end. <c>LEN</c> as the length works because it is never
    /// smaller than what is left from <c>i</c>, and <c>MID</c> returns whatever is there.
    /// </summary>
    [Fact]
    public void SubstringWithOneArgument_GoesToTheEnd()
    {
        string dax = Flat(Table().Where(f => f.Periodo.Substring(3) == "2024").ToDaxString());

        Assert.Contains("MID(Fato[Periodo], 4, LEN(Fato[Periodo]))", dax, StringComparison.Ordinal);
    }

    // ---------- the conversion ----------

    [Theory]
    [InlineData("int")]
    [InlineData("long")]
    [InlineData("decimal")]
    [InlineData("double")]
    public void ParseOverAColumn_BecomesValue(string tipo)
    {
        string dax = Flat((tipo switch
        {
            "int" => Table().Where(f => int.Parse(f.Periodo) > 0),
            "long" => Table().Where(f => long.Parse(f.Periodo) > 0),
            "decimal" => Table().Where(f => decimal.Parse(f.Periodo) > 0),
            _ => Table().Where(f => double.Parse(f.Periodo) > 0)
        }).ToDaxString());

        Assert.Contains("VALUE(Fato[Periodo])", dax, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToNumberOverText_BecomesValueToo()
    {
        string dax = Flat(Table().Where(f => Convert.ToInt32(f.Periodo) > 0).ToDaxString());

        Assert.Contains("VALUE(Fato[Periodo])", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>year * 100 + month</c> key over an <c>MM-yyyy</c> period, built entirely in DAX —
    /// which is what could not be taken to the server before.
    /// </summary>
    [Fact]
    public void ThePeriodSortKey_TranslatesWhole()
    {
        string dax = Flat(Table()
            .Where(f => ((int.Parse(f.Periodo.Substring(3)) * 100)
                         + int.Parse(f.Periodo.Substring(0, 2))) > 202412)
            .ToDaxString());

        Assert.Contains(
            "VALUE(MID(Fato[Periodo], 4, LEN(Fato[Periodo]))) * 100 "
            + "+ VALUE(MID(Fato[Periodo], 1, 2)) > 202412",
            dax,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And now there is somewhere to use it: the most recent period comes out <b>sorted on the
    /// server</b>, which is the fourth criterion, and what the conversion alone did not close.
    /// </summary>
    /// <remarks>
    /// This test used to be the inverse — it pinned down the refusal of
    /// <c>OrderBy(x =&gt; expression)</c>, because <c>DaxOrderTerm</c> carried a column reference.
    /// It came to carry an expression, and the test became what it should always have been.
    /// </remarks>
    [Fact]
    public void OrderingByThePeriodKey_HappensOnTheServer()
    {
        string dax = Flat(Table()
            .OrderByDescending(f => (int.Parse(f.Periodo.Substring(3)) * 100)
                                    + int.Parse(f.Periodo.Substring(0, 2)))
            .Take(3)
            .ToDaxString());

        // The expression goes in BOTH places: as the TOPN's ordering argument, which chooses which
        // rows come in, and in the ORDER BY clause, which is the only thing guaranteeing the output's order.
        Assert.Equal(
            "EVALUATE TOPN(3, Fato, "
            + "VALUE(MID(Fato[Periodo], 4, LEN(Fato[Periodo]))) * 100 "
            + "+ VALUE(MID(Fato[Periodo], 1, 2)), DESC) "
            + "ORDER BY VALUE(MID(Fato[Periodo], 4, LEN(Fato[Periodo]))) * 100 "
            + "+ VALUE(MID(Fato[Periodo], 1, 2)) DESC",
            dax);
    }

    /// <summary>
    /// From a captured variable it still becomes a literal: it does not depend on the parameter, so
    /// it is evaluated during translation and the server sees no conversion at all.
    /// </summary>
    [Fact]
    public void ParseOfACapturedVariable_StillBecomesALiteral()
    {
        string texto = "42";
        string dax = Flat(Table().Where(f => f.Valor > int.Parse(texto)).ToDaxString());

        Assert.Contains("Fato[Valor] > 42", dax, StringComparison.Ordinal);
        Assert.DoesNotContain("VALUE(", dax, StringComparison.Ordinal);
    }

    // ---------- what is refused ----------

    /// <summary>
    /// With an <c>IFormatProvider</c> it is refused. <c>VALUE</c> reads the text by the model's
    /// locale, and there is no way to impose a culture on the server — translating it anyway would
    /// silently discard the very argument the author passed in order not to depend on the culture.
    /// </summary>
    [Fact]
    public void ParseWithAFormatProvider_IsRefusedNamingWhy()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table()
                .Where(f => decimal.Parse(f.Periodo, System.Globalization.CultureInfo.InvariantCulture) > 0)
                .ToDaxString());

        Assert.Contains("IFormatProvider", ex.Message, StringComparison.Ordinal);
        Assert.Contains("locale", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Convert.ToInt32</c> over a number rounds — and to the nearest even. <c>VALUE</c> does not
    /// do that and <c>ROUND</c> does not reproduce it, so it is refused rather than approximated.
    /// </summary>
    [Fact]
    public void ConvertToNumberOverANumber_IsRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table().Where(f => Convert.ToInt32(f.Valor) > 0).ToDaxString());

        Assert.Contains("rounds", ex.Message, StringComparison.Ordinal);
    }

    // ---------- TryParse ----------

    /// <summary>
    /// A field, not a local variable, because the two <c>out</c> forms the compiler accepts inside
    /// an expression tree have to exist somewhere — see
    /// <see cref="TryParseOverAColumn_IsRefusedWithItsOwnReason"/>.
    /// </summary>
    private static int _parsed;

    /// <summary>
    /// <c>TryParse</c> has a refusal of <b>its own</b>, not the generic untranslatable-method one:
    /// the reason is not a missing implementation, it is two semantic incompatibilities no
    /// implementation would remove — the <c>out</c> parameter has nowhere to go in an expression
    /// that returns one value, and the promise <b>not to throw</b> is the opposite of what
    /// <c>VALUE</c> does on text that does not convert.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is not an unreachable case.</b> The compiler refuses an inline-declared <c>out int v</c>
    /// (CS8198) and the discard <c>out _</c> (CS8207) inside an expression tree, which might
    /// suggest <c>TryParse</c> never reaches the translator. It does: an <c>out</c> over an
    /// <b>already-declared</b> variable or over a <b>field</b> compiles, and that is what this test uses.
    /// </para>
    /// <para>
    /// The message says to use <c>Parse</c>, so it names the type — the advice has to be actionable
    /// on the type the author actually used.
    /// </para>
    /// </remarks>
    [Fact]
    public void TryParseOverAColumn_IsRefusedWithItsOwnReason()
    {
        int local = 0;

        NotSupportedException porLocal = Assert.Throws<NotSupportedException>(
            () => Table().Where(f => int.TryParse(f.Periodo, out local)).ToDaxString());

        NotSupportedException porCampo = Assert.Throws<NotSupportedException>(
            () => Table().Where(f => int.TryParse(f.Periodo, out _parsed)).ToDaxString());

        foreach (NotSupportedException ex in new[] { porLocal, porCampo })
        {
            Assert.Contains("Int32.TryParse", ex.Message, StringComparison.Ordinal);
            Assert.Contains("out parameter", ex.Message, StringComparison.Ordinal);
            Assert.Contains("not to throw", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Int32.Parse", ex.Message, StringComparison.Ordinal);

            // And NOT the generic refusal, which would only say "is not supported in DAX translation".
            Assert.DoesNotContain("is not supported in DAX", ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <c>DateTime.TryParse</c> still falls into the <b>generic</b> refusal, and that is deliberate:
    /// the bespoke message says to use <c>Parse</c>, and there <c>Parse</c> has no translation
    /// either — telling someone to switch to something that also does not work is worse than saying nothing.
    /// </summary>
    [Fact]
    public void TryParseOfATypeWhoseParseIsNotTranslated_KeepsTheGenericRefusal()
    {
        DateTime local = default;

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table().Where(f => DateTime.TryParse(f.Periodo, out local)).ToDaxString());

        Assert.Contains("TryParse", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("out parameter", ex.Message, StringComparison.Ordinal);
    }
}
