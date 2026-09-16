using System.Globalization;
using System.Linq.Expressions;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Syntax;
using PowerLinq.DaxConverter.Translators;

namespace PowerLinq.Tests.Query;

/// <summary>
/// A single check — "does this subtree depend on the lambda's parameter?" — decides both sides:
/// if it does and there is no translation, the error has to say so clearly; if it does not, it is
/// constant and can be evaluated during translation instead of demanding an intermediate variable.
/// </summary>
public sealed class DaxParameterDependencyTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("Produto[Preco]")] public decimal Preco { get; set; }
        [DaxColumn("Produto[Data]")] public DateTime Data { get; set; }
        [DaxColumn("Produto[Opcional]")] public int? Opcional { get; set; }
    }

    private static string Translate(Expression body) =>
        new DaxExpressionVisitor("Produto").Translate(body).ToDaxString();

    private static NotSupportedException Refused(Expression<Func<Produto, bool>> predicate) =>
        Assert.Throws<NotSupportedException>(() => Translate(predicate.Body));

    // ---------- a nested member refused clearly ----------

    // Members that still have no direct DAX equivalent. `Length` and the date parts left this
    // list once they started being translated — the corresponding refusal tests became
    // behaviour tests in DaxDatePartAndTextTests.
    [Fact]
    public void NestedMember_DayOfYear_IsRefusedNamingThePath()
    {
        Expression<Func<Produto, bool>> predicate = p => p.Data.DayOfYear > 3;

        NotSupportedException ex = Refused(predicate);

        Assert.StartsWith("Produto.Data.DayOfYear has no DAX translation", ex.Message);
    }

    [Fact]
    public void NestedMember_Ticks_IsRefusedNamingThePath()
    {
        Expression<Func<Produto, bool>> predicate = p => p.Data.Ticks > 0;

        NotSupportedException ex = Refused(predicate);

        Assert.StartsWith("Produto.Data.Ticks has no DAX translation", ex.Message);
    }

    [Fact]
    public void NestedMember_IsNotTheRuntimeCompileError()
    {
        // The flow used to fall into an Expression.Lambda(node).Compile() over a free parameter and
        // blew up with InvalidOperationException("variable 'p' ... is not defined") — a runtime
        // message, saying neither which member nor that the cause is a missing translation.
        Expression<Func<Produto, bool>> predicate = p => p.Data.Ticks > 0;

        Exception ex = Record.Exception(() => Translate(predicate.Body))!;

        Assert.IsType<NotSupportedException>(ex);
        Assert.DoesNotContain("referenced from scope", ex.Message);
    }

    [Fact]
    public void RefusalMessage_IsLocalized()
    {
        Expression<Func<Produto, bool>> predicate = p => p.Data.Ticks > 0;
        IPowerLinqLocalizer portuguese = new ResourceManagerPowerLinqLocalizer("pt-BR");

        Assert.StartsWith(
            "Produto.Data.Ticks não tem tradução para DAX",
            portuguese.Format("MemberNotTranslatable", "Produto.Data.Ticks"));

        // And the real path refuses too, in the default language.
        Assert.StartsWith("Produto.Data.Ticks has no", Refused(predicate).Message);
    }

    // ---------- a closed subexpression folded into a literal ----------

    [Fact]
    public void NewDateTime_IsFoldedIntoADateLiteral()
    {
        // Before: NotSupportedException("Expression 'New' is not supported"), and the README said
        // to extract it into a variable.
        Expression<Func<Produto, bool>> predicate = p => p.Data >= new DateTime(2024, 1, 1);

        Assert.Equal("Produto[Data] >= DATE(2024,1,1)", Translate(predicate.Body));
    }

    [Fact]
    public void ClosedMethodCall_IsFoldedIntoALiteral()
    {
        Expression<Func<Produto, bool>> predicate =
            p => p.Preco > decimal.Parse("1.5", CultureInfo.InvariantCulture);

        Assert.Equal("Produto[Preco] > 1.5", Translate(predicate.Body));
    }

    [Fact]
    public void ClosedDateArithmetic_IsFoldedIntoADateLiteral()
    {
        Expression<Func<Produto, bool>> predicate = p => p.Data >= DateTime.Today.AddDays(-30);

        string dax = Translate(predicate.Body);

        Assert.StartsWith("Produto[Data] >= DATE(", dax);
    }

    [Fact]
    public void ClosedStaticProperty_IsFoldedIntoALiteral()
    {
        Expression<Func<Produto, bool>> predicate = p => p.Data <= DateTime.MaxValue;

        Assert.StartsWith("Produto[Data] <= DATE(", Translate(predicate.Body));
    }

    [Fact]
    public void ClosedStringPredicate_IsFoldedIntoABooleanLiteral()
    {
        // The text comes from outside and does not depend on the row: resolving it here spares the
        // server from evaluating ISBLANK over a literal.
        string vazio = "";
        Expression<Func<Produto, bool>> predicate = p => string.IsNullOrEmpty(vazio);

        Assert.Equal("TRUE", Translate(predicate.Body));
    }

    [Fact]
    public void ClosedNewGuid_IsFoldedIntoAText()
    {
        var esperado = new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        Expression<Func<Produto, bool>> predicate =
            p => p.Nome == new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff").ToString();

        Assert.Equal($"Produto[Nome] = \"{esperado}\"", Translate(predicate.Body));
    }

    // ---------- what already worked keeps working ----------

    [Fact]
    public void CapturedVariable_StillBecomesALiteral()
    {
        var inicio = new DateTime(2024, 1, 1);
        Expression<Func<Produto, bool>> predicate = p => p.Data >= inicio;

        Assert.Equal("Produto[Data] >= DATE(2024,1,1)", Translate(predicate.Body));
    }

    [Fact]
    public void DirectColumnAccess_IsUnchanged()
    {
        Expression<Func<Produto, bool>> predicate = p => p.Nome == "abc";

        Assert.Equal("Produto[Nome] = \"abc\"", Translate(predicate.Body));
    }

    [Fact]
    public void StringMethodOnAColumn_IsStillTranslated()
    {
        // It depends on the parameter, so it is not folded — it goes through normal translation.
        Expression<Func<Produto, bool>> predicate = p => p.Nome.StartsWith("AB");

        Assert.Equal("LEFT(Produto[Nome], LEN(\"AB\")) = \"AB\"", Translate(predicate.Body));
    }

    [Fact]
    public void StringMethodWithACapturedArgument_IsStillTranslated()
    {
        string prefixo = "AB";
        Expression<Func<Produto, bool>> predicate = p => p.Nome.StartsWith(prefixo);

        Assert.Equal("LEFT(Produto[Nome], LEN(\"AB\")) = \"AB\"", Translate(predicate.Body));
    }

    [Fact]
    public void UnsupportedMethodOnAColumn_StillReportsTheMethod()
    {
        // It depends on the parameter and has no translation: still a method error, not a folding one.
        // Substring left this role once it gained a translation; PadLeft still has no direct equivalent.
        Expression<Func<Produto, bool>> predicate = p => p.Nome.PadLeft(5) == "A";

        NotSupportedException ex = Refused(predicate);

        Assert.Contains("PadLeft", ex.Message);
    }
}
