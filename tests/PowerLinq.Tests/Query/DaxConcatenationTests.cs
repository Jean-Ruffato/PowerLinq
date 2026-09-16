using System.Linq.Expressions;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Syntax;
using PowerLinq.DaxConverter.Translators;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Text concatenation uses DAX's <c>&amp;</c> operator, not <c>+</c>.
/// </summary>
/// <remarks>
/// <para>
/// Concatenation used to emit the <b>arithmetic</b> <c>+</c>. The reason is subtle: in an
/// expression tree, <c>a + b</c> over <c>string</c> does not become <c>string.Concat</c> — it
/// becomes an <see cref="ExpressionType.Add"/> with the <c>Concat</c> method attached. So it fell
/// into <c>VisitBinary</c> alongside numeric addition.
/// </para>
/// <para>
/// In DAX the <c>+</c> converts to a number: <c>"1" + "2"</c> is <b>3</b>, not <c>"12"</c>. For a
/// text column with numeric content — a code, a period, a document — the result was silently
/// wrong, with no error from the server.
/// </para>
/// </remarks>
public sealed class DaxConcatenationTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("Produto[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Preco]")] public decimal Preco { get; set; }
    }

    private static string Translate(Expression body) =>
        new DaxExpressionVisitor("Produto").Translate(body).ToDaxString();

    // ---------- concatenation ----------

    [Fact]
    public void TwoColumns_UseTheAmpersand()
    {
        Expression<Func<Produto, string>> selector = p => p.Nome + p.Categoria;

        Assert.Equal("Produto[Nome] & Produto[Categoria]", Translate(selector.Body));
    }

    [Fact]
    public void ThreeOperands_ChainLeftToRight()
    {
        Expression<Func<Produto, string>> selector = p => p.Nome + " - " + p.Categoria;

        Assert.Equal("Produto[Nome] & \" - \" & Produto[Categoria]", Translate(selector.Body));
    }

    [Fact]
    public void ManyOperands_StayFlat()
    {
        Expression<Func<Produto, string>> selector = p => p.Nome + "a" + p.Categoria + "b" + p.Nome;

        Assert.Equal(
            "Produto[Nome] & \"a\" & Produto[Categoria] & \"b\" & Produto[Nome]",
            Translate(selector.Body));
    }

    [Fact]
    public void NumericOperand_IsConcatenatedNotAdded()
    {
        // The case that exposes the bug: `+` would add, `&` concatenates — and this is what C# does.
        Expression<Func<Produto, string>> selector = p => p.Nome + p.Id;

        Assert.Equal("Produto[Nome] & Produto[Id]", Translate(selector.Body));
    }

    [Fact]
    public void ExplicitConcat_BecomesTheSameChain()
    {
        Expression<Func<Produto, string>> selector = p => string.Concat(p.Nome, " ", p.Categoria);

        Assert.Equal("Produto[Nome] & \" \" & Produto[Categoria]", Translate(selector.Body));
    }

    [Fact]
    public void ConcatWithAnArrayArgument_IsUnwrapped()
    {
        // The overload taking string[] arrives as a single array argument, which has to be opened
        // up instead of treated as a value.
        //
        // Note: the `string.Concat(a, b, c, d, e)` shape is not testable here — in modern .NET it
        // resolves to the `params ReadOnlySpan<string>` overload, and an expression tree cannot
        // contain a ref struct (CS8640). The explicit array is the reachable shape.
        Expression<Func<Produto, string>> selector =
            p => string.Concat(new[] { p.Nome, "a", p.Categoria });

        Assert.Equal("Produto[Nome] & \"a\" & Produto[Categoria]", Translate(selector.Body));
    }

    // ---------- numeric addition is unaffected ----------

    [Fact]
    public void NumericAddition_StaysArithmetic()
    {
        Expression<Func<Produto, decimal>> selector = p => p.Preco + 10m;

        Assert.Equal("Produto[Preco] + 10", Translate(selector.Body));
    }

    [Fact]
    public void IntegerAddition_StaysArithmetic()
    {
        Expression<Func<Produto, int>> selector = p => p.Id + 1;

        Assert.Equal("Produto[Id] + 1", Translate(selector.Body));
    }

    [Fact]
    public void MixedArithmeticAndConcatenation_KeepEachOperator()
    {
        Expression<Func<Produto, string>> selector = p => p.Nome + (p.Id + 1);

        Assert.Equal("Produto[Nome] & Produto[Id] + 1", Translate(selector.Body));
    }

    // ---------- precedence ----------

    [Fact]
    public void ComparedAgainstALiteral_NeedsNoParentheses()
    {
        // `&` has precedence 4, comparison has 3, so the chain is not parenthesized.
        Expression<Func<Produto, bool>> predicate = p => p.Nome + p.Categoria == "AB";

        Assert.Equal("Produto[Nome] & Produto[Categoria] = \"AB\"", Translate(predicate.Body));
    }

    [Fact]
    public void InsideAPredicateWithAnd_ComposesCleanly()
    {
        Expression<Func<Produto, bool>> predicate =
            p => p.Nome + "-" + p.Categoria == "A-B" && p.Id > 0;

        Assert.Equal(
            "Produto[Nome] & \"-\" & Produto[Categoria] = \"A-B\" && Produto[Id] > 0",
            Translate(predicate.Body));
    }

    [Fact]
    public void WrappedInATextFunction_Works()
    {
        Expression<Func<Produto, string>> selector = p => (p.Nome + p.Categoria).ToUpper();

        Assert.Equal("UPPER(Produto[Nome] & Produto[Categoria])", Translate(selector.Body));
    }

    [Fact]
    public void ComposesWithTheOtherTextTranslations()
    {
        // Building a label out of a slice is the real case, and it only became possible with the
        // two translations together: MID comes from the text methods, & comes from here.
        Expression<Func<Produto, string>> selector =
            p => p.Nome.Substring(0, 3) + " - " + p.Categoria.ToUpper();

        Assert.Equal(
            "MID(Produto[Nome], 1, 3) & \" - \" & UPPER(Produto[Categoria])",
            Translate(selector.Body));
    }

    // ---------- constants ----------

    [Fact]
    public void TwoLiterals_AreFoldedBeforeReachingTheOperator()
    {
        // Nothing depends on the row, so the closed-subexpression fold resolves it first.
        Expression<Func<Produto, bool>> predicate = p => p.Nome == "a" + "b";

        Assert.Equal("Produto[Nome] = \"ab\"", Translate(predicate.Body));
    }

    [Fact]
    public void CapturedValueInTheChain_BecomesALiteral()
    {
        string separador = " | ";
        Expression<Func<Produto, string>> selector = p => p.Nome + separador + p.Categoria;

        Assert.Equal("Produto[Nome] & \" | \" & Produto[Categoria]", Translate(selector.Body));
    }

    // ---------- interpolation ----------

    [Fact]
    public void Interpolation_IsRefusedPointingToConcatenation()
    {
        // The compiler cannot use the interpolation handler inside an expression tree, so it falls
        // back to string.Format — whose format string DAX does not express.
        Expression<Func<Produto, string>> selector = p => $"{p.Nome} - {p.Categoria}";

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Translate(selector.Body));

        Assert.Contains("String interpolation is not translated", ex.Message);
        Assert.Contains("+", ex.Message);
    }

    [Fact]
    public void InterpolationWithANumber_IsAlsoRefused()
    {
        Expression<Func<Produto, string>> selector = p => $"{p.Nome}-{p.Id}";

        Assert.Throws<NotSupportedException>(() => Translate(selector.Body));
    }

    [Fact]
    public void InterpolationMessage_IsLocalized()
    {
        IPowerLinqLocalizer portuguese = new ResourceManagerPowerLinqLocalizer("pt-BR");

        Assert.Contains(
            "Interpolação de string não é traduzida",
            portuguese.Get("StringInterpolationUnsupported"));
    }
}
