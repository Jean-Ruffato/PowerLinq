using System.Globalization;
using System.Linq.Expressions;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Syntax;
using PowerLinq.DaxConverter.Translators;

namespace PowerLinq.Tests.Query;

public class DaxExpressionVisitorTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[ProdutoID]")]
        public int ProdutoId { get; set; }

        [DaxColumn("Produto[Nome]")]
        public string Nome { get; set; } = string.Empty;

        [DaxColumn("Produto[Categoria]")]
        public string Categoria { get; set; } = string.Empty;

        [DaxColumn("Produto[Preco]")]
        public decimal Preco { get; set; }

        [DaxColumn("Produto[Ativo]")]
        public bool Ativo { get; set; }

        /// <summary>No attribute: it exercises the Produto[Observacao] fallback.</summary>
        public string Observacao { get; set; } = string.Empty;
    }

    private static DaxExpressionVisitor Visitor => new("Produto");

    /// <summary>Translates and renders — the comparison equivalent to the old API.</summary>
    private static string TranslateToDax(Expression expression) =>
        Visitor.Translate(expression).ToDaxString();

    [Fact]
    public void Translate_EqualityString_ProducesDaxEquals()
    {
        Expression<Func<Produto, bool>> expr = p => p.Categoria == "Eletrônicos";
        string result = TranslateToDax(expr.Body);

        Assert.Equal("Produto[Categoria] = \"Eletrônicos\"", result);
    }

    [Fact]
    public void Translate_EqualityInt_ProducesDaxEquals()
    {
        Expression<Func<Produto, bool>> expr = p => p.ProdutoId == 42;
        string result = TranslateToDax(expr.Body);

        Assert.Equal("Produto[ProdutoID] = 42", result);
    }

    [Fact]
    public void Translate_AndAlso_ProducesDoubleAmpersand()
    {
        Expression<Func<Produto, bool>> expr = p => p.Categoria == "X" && p.Ativo == true;
        string result = TranslateToDax(expr.Body);

        Assert.Equal("Produto[Categoria] = \"X\" && Produto[Ativo] = TRUE", result);
    }

    [Fact]
    public void Translate_OrElse_ProducesDoublePipe()
    {
        Expression<Func<Produto, bool>> expr = p => p.Categoria == "X" || p.Categoria == "Y";
        string result = TranslateToDax(expr.Body);

        Assert.Equal("Produto[Categoria] = \"X\" || Produto[Categoria] = \"Y\"", result);
    }

    [Fact]
    public void Translate_GreaterThan_ProducesGreaterThan()
    {
        Expression<Func<Produto, bool>> expr = p => p.Preco > 100;
        string result = TranslateToDax(expr.Body);

        Assert.Contains("> 100", result);
    }

    [Fact]
    public void Translate_CapturedVariable_EvaluatesAtTranslationTime()
    {
        string categoria = "Móveis";
        Expression<Func<Produto, bool>> expr = p => p.Categoria == categoria;
        string result = TranslateToDax(expr.Body);

        Assert.Equal("Produto[Categoria] = \"Móveis\"", result);
    }

    [Fact]
    public void Translate_NullValue_ProducesBlank()
    {
        string? nullCategoria = null;
        Expression<Func<Produto, bool>> expr = p => p.Nome == nullCategoria;
        string result = TranslateToDax(expr.Body);

        Assert.Contains("BLANK()", result);
    }

    [Fact]
    public void Translate_BoolProperty_ProducesTrue()
    {
        Expression<Func<Produto, bool>> expr = p => p.Ativo == true;
        string result = TranslateToDax(expr.Body);

        Assert.Contains("TRUE", result);
    }

    [Fact]
    public void Translate_StringWithDoubleQuotes_EscapesQuotes()
    {
        Expression<Func<Produto, bool>> expr = p => p.Nome == "O'Brien \"Test\"";
        string result = TranslateToDax(expr.Body);

        Assert.Contains("\"\"", result);
    }

    [Fact]
    public void Translate_Not_ProducesNotFunction()
    {
        Expression<Func<Produto, bool>> expr = p => !(p.Categoria == "X");
        string result = TranslateToDax(expr.Body);

        Assert.Equal("NOT(Produto[Categoria] = \"X\")", result);
    }

    [Fact]
    public void Translate_PropertyWithoutAttribute_FallsBackToTableName()
    {
        Expression<Func<Produto, bool>> expr = p => p.Observacao == "nota";
        string result = TranslateToDax(expr.Body);

        Assert.Equal("Produto[Observacao] = \"nota\"", result);
    }

    [Fact]
    public void Translate_ReturnsTypedNodes_NotStrings()
    {
        Expression<Func<Produto, bool>> expr = p => p.ProdutoId == 42;

        DaxBinary binary = Assert.IsType<DaxBinary>(Visitor.Translate(expr.Body));

        Assert.Equal(DaxOperator.Equal, binary.Operator);
        Assert.Equal("Produto[ProdutoID]", Assert.IsType<DaxColumnRef>(binary.Left).Reference);
        Assert.Equal(42, Assert.IsType<DaxNumberLiteral>(binary.Right).Value);
    }

    [Fact]
    public void From_SelectsLiteralNodeByValueType()
    {
        // The value's type is inspected once, at construction: after that each literal writes
        // itself without deciding anything.
        Assert.IsType<DaxTextLiteral>(DaxLiteral.From("x"));
        Assert.IsType<DaxNumberLiteral>(DaxLiteral.From(100.5m));
        Assert.IsType<DaxDateLiteral>(DaxLiteral.From(new DateTime(2024, 1, 1)));
        Assert.Same(DaxBooleanLiteral.True, DaxLiteral.From(true));
        Assert.Same(DaxBlankLiteral.Instance, DaxLiteral.From(null));
    }

    /// <summary>
    /// The date types that are not <see cref="DateTime"/> fall into the same <c>DATE(a,b,c)</c>,
    /// and a <see cref="char"/> becomes one-character text — not a number, which is what a
    /// carelessly converted <c>char</c> would become.
    /// </summary>
    [Fact]
    public void From_HandlesCharAndTheOtherDateTypes()
    {
        Assert.Equal("\"A\"", DaxLiteral.From('A').ToDaxString());

        Assert.Equal(
            "DATE(2026,8,30)",
            DaxLiteral.From(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero)).ToDaxString());

        Assert.Equal("DATE(2026,8,30)", DaxLiteral.From(new DateOnly(2026, 8, 30)).ToDaxString());
    }

    /// <summary>
    /// An unknown type becomes <b>text</b>, not a bare token: a token would be a syntax error on
    /// the server, whereas a string is at worst a type mismatch — which the server reports while
    /// saying what happened.
    /// </summary>
    [Fact]
    public void From_TurnsAnUnknownTypeIntoText()
    {
        var guid = Guid.NewGuid();

        IDaxExpression literal = DaxLiteral.From(guid);

        Assert.IsType<DaxTextLiteral>(literal);
        Assert.Equal($"\"{guid}\"", literal.ToDaxString());
    }

    // ------------------------------------------------------- string methods

    [Fact]
    public void Translate_IsNullOrWhiteSpace_ProducesIsBlankOrTrimEmpty()
    {
        Expression<Func<Produto, bool>> expr = p => string.IsNullOrWhiteSpace(p.Nome);
        string result = TranslateToDax(expr.Body);

        Assert.Equal("ISBLANK(Produto[Nome]) || TRIM(Produto[Nome]) = \"\"", result);
    }

    [Fact]
    public void Translate_IsNullOrEmpty_ProducesIsBlankOrEmpty()
    {
        Expression<Func<Produto, bool>> expr = p => string.IsNullOrEmpty(p.Nome);
        string result = TranslateToDax(expr.Body);

        Assert.Equal("ISBLANK(Produto[Nome]) || Produto[Nome] = \"\"", result);
    }

    [Fact]
    public void Translate_NotIsNullOrWhiteSpace_ProducesNegatedForm()
    {
        // The case that used to blow up with "Expression 'Call' is not supported".
        Expression<Func<Produto, bool>> expr = p => !string.IsNullOrWhiteSpace(p.Nome);
        string result = TranslateToDax(expr.Body);

        Assert.Equal("NOT(ISBLANK(Produto[Nome]) || TRIM(Produto[Nome]) = \"\")", result);
    }

    [Fact]
    public void Translate_ToUpper_ProducesUpper()
    {
        Expression<Func<Produto, bool>> expr = p => p.Categoria.ToUpper() == "X";
        string result = TranslateToDax(expr.Body);

        Assert.Equal("UPPER(Produto[Categoria]) = \"X\"", result);
    }

    [Fact]
    public void Translate_ToLower_ProducesLower()
    {
        Expression<Func<Produto, bool>> expr = p => p.Categoria.ToLower() == "x";
        string result = TranslateToDax(expr.Body);

        Assert.Equal("LOWER(Produto[Categoria]) = \"x\"", result);
    }

    [Fact]
    public void Translate_StartsWith_ProducesLeftMatch()
    {
        Expression<Func<Produto, bool>> expr = p => p.Nome.StartsWith("AB");
        string result = TranslateToDax(expr.Body);

        Assert.Equal("LEFT(Produto[Nome], LEN(\"AB\")) = \"AB\"", result);
    }

    [Fact]
    public void Translate_EndsWith_ProducesRightMatch()
    {
        Expression<Func<Produto, bool>> expr = p => p.Nome.EndsWith("XY");
        string result = TranslateToDax(expr.Body);

        Assert.Equal("RIGHT(Produto[Nome], LEN(\"XY\")) = \"XY\"", result);
    }

    [Fact]
    public void Translate_Contains_ProducesSearch()
    {
        Expression<Func<Produto, bool>> expr = p => p.Nome.Contains("mid");
        string result = TranslateToDax(expr.Body);

        Assert.Equal("SEARCH(\"mid\", Produto[Nome], 1, 0) > 0", result);
    }

    [Fact]
    public void Translate_UnsupportedMethod_Throws()
    {
        // Substring came to be translated into MID; PadLeft still has no direct equivalent,
        // because it would require composing REPT with the concatenation operator.
        Expression<Func<Produto, bool>> expr = p => p.Nome.PadLeft(5) == "A";

        Assert.Throws<NotSupportedException>(() => TranslateToDax(expr.Body));
    }

    // ------------------------------------------------------------- precedence

    [Fact]
    public void Translate_OrInsideAnd_ParenthesizesOr()
    {
        Expression<Func<Produto, bool>> expr = p =>
            (p.Categoria == "X" || p.Categoria == "Y") && p.Ativo == true;

        string result = TranslateToDax(expr.Body);

        // || has lower precedence than &&, so it needs the parentheses
        Assert.Equal(
            "(Produto[Categoria] = \"X\" || Produto[Categoria] = \"Y\") && Produto[Ativo] = TRUE",
            result);
    }

    [Fact]
    public void Translate_AndInsideOr_OmitsRedundantParentheses()
    {
        Expression<Func<Produto, bool>> expr = p =>
            (p.Categoria == "X" && p.Ativo == true) || p.ProdutoId == 1;

        string result = TranslateToDax(expr.Body);

        // && binds tighter than ||: the parentheses would be redundant
        Assert.Equal(
            "Produto[Categoria] = \"X\" && Produto[Ativo] = TRUE || Produto[ProdutoID] = 1",
            result);
    }

    [Fact]
    public void Translate_ChainedAnd_OmitsRedundantParentheses()
    {
        Expression<Func<Produto, bool>> expr = p =>
            p.Categoria == "X" && p.Ativo == true && p.ProdutoId == 1;

        string result = TranslateToDax(expr.Body);

        Assert.Equal(
            "Produto[Categoria] = \"X\" && Produto[Ativo] = TRUE && Produto[ProdutoID] = 1",
            result);
    }

    [Fact]
    public void Translate_ArithmeticInsideComparison_OmitsRedundantParentheses()
    {
        Expression<Func<Produto, bool>> expr = p => p.Preco * 2 > 100m;
        string result = TranslateToDax(expr.Body);

        // * binds tighter than >, so nothing needs parentheses
        Assert.Equal("Produto[Preco] * 2 > 100", result);
    }

    [Fact]
    public void Translate_NonAssociativeRightOperand_KeepsParentheses()
    {
        // a - (b - c) is not equivalent to a - b - c
        var inner = new DaxBinary(
            DaxOperator.Subtract, DaxLiteral.From(2), DaxLiteral.From(3));
        var outer = new DaxBinary(
            DaxOperator.Subtract, DaxLiteral.From(1), inner);

        Assert.Equal("1 - (2 - 3)", outer.ToDaxString());
    }

    // ---------------------------------------------------------------- culture

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("")]
    public void Translate_DecimalLiteral_UsesInvariantCultureRegardlessOfCurrentCulture(
        string cultureName)
    {
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

            Expression<Func<Produto, bool>> expr = p => p.Preco > 100.5m;
            string result = TranslateToDax(expr.Body);

            // Under pt-BR/de-DE the decimal separator would be a comma, which in DAX
            // separates arguments and would break the query on the server.
            Assert.Equal("Produto[Preco] > 100.5", result);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Write_DoubleLiteral_UsesInvariantCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");

            var expression = new DaxBinary(
                DaxOperator.LessThan, new DaxColumnRef("Produto[Taxa]"), DaxLiteral.From(0.075d));

            Assert.Equal("Produto[Taxa] < 0.075", expression.ToDaxString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Write_DateTimeLiteral_ProducesDateFunction()
    {
        IDaxExpression expression = DaxLiteral.From(new DateTime(2024, 3, 7));

        Assert.Equal("DATE(2024,3,7)", expression.ToDaxString());
    }

    // --------------------------------------------------- remaining operators

    [Fact]
    public void Translate_LessThan_ProducesDaxLessThan()
    {
        Expression<Func<Produto, bool>> expr = p => p.Preco < 10m;

        Assert.Equal("Produto[Preco] < 10", TranslateToDax(expr.Body));
    }

    [Fact]
    public void Translate_Divide_ProducesDaxDivide()
    {
        Expression<Func<Produto, decimal>> expr = p => p.Preco / 2m;

        Assert.Equal("Produto[Preco] / 2", TranslateToDax(expr.Body));
    }

    /// <summary>
    /// A binary operator with no equivalent is refused while naming which one. Staying silent here
    /// would be worse than the exception: the DAX would come out with another operator's semantics.
    /// </summary>
    [Fact]
    public void Translate_AnUnsupportedBinaryOperator_IsRefusedNamingIt()
    {
        Expression<Func<Produto, bool>> expr = p => p.ProdutoId % 2 == 0;

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => TranslateToDax(expr.Body));

        Assert.Contains("Modulo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_AnUnsupportedUnaryOperator_IsRefusedNamingIt()
    {
        Expression<Func<Produto, decimal>> expr = p => -p.Preco;

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => TranslateToDax(expr.Body));

        Assert.Contains("Negate", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <see cref="string"/> static outside the translated list is refused while naming the method
    /// — and not confused with one that does translate just because it has the same call shape.
    /// </summary>
    [Fact]
    public void Translate_AnUntranslatedStaticStringMethod_IsRefusedNamingIt()
    {
        Expression<Func<Produto, bool>> expr = p => string.Equals(p.Nome, "x", StringComparison.Ordinal);

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => TranslateToDax(expr.Body));

        Assert.Contains("Equals", ex.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------------- instance methods

    [Fact]
    public void Translate_Trim_ProducesTrim()
    {
        Expression<Func<Produto, string>> expr = p => p.Nome.Trim();

        Assert.Equal("TRIM(Produto[Nome])", TranslateToDax(expr.Body));
    }

    /// <summary><c>ToString()</c> over text is the identity: it crosses the operand.</summary>
    [Fact]
    public void Translate_ToStringOverText_IsIdentity()
    {
        Expression<Func<Produto, string>> expr = p => p.Nome.ToString();

        Assert.Equal("Produto[Nome]", TranslateToDax(expr.Body));
    }

    // -------------------------------------------- captured value evaluation

    private sealed class Filtro
    {
        public string Categoria { get; set; } = "";
    }

    private sealed class Envelope
    {
        public Filtro Interno { get; set; } = new();
    }

    /// <summary>
    /// A captured object's property is read by reflection, without compiling the expression — the
    /// cheap path. Only what does not resolve that way falls into the <c>Compile</c>.
    /// </summary>
    [Fact]
    public void Translate_APropertyOnACapturedObject_IsEvaluated()
    {
        var filtro = new Filtro { Categoria = "Eletrônicos" };
        Expression<Func<Produto, bool>> expr = p => p.Categoria == filtro.Categoria;

        Assert.Equal("Produto[Categoria] = \"Eletrônicos\"", TranslateToDax(expr.Body));
    }

    /// <summary>A member over a member over the closure: the read descends recursively.</summary>
    [Fact]
    public void Translate_ANestedPropertyOnACapturedObject_IsEvaluated()
    {
        var envelope = new Envelope { Interno = new Filtro { Categoria = "Móveis" } };
        Expression<Func<Produto, bool>> expr = p => p.Categoria == envelope.Interno.Categoria;

        Assert.Equal("Produto[Categoria] = \"Móveis\"", TranslateToDax(expr.Body));
    }

    private static Filtro CriarFiltro() => new() { Categoria = "Vinda de método" };

    /// <summary>
    /// When the member's owner is neither a constant nor another member — here, a method's return
    /// value — the reflection read cannot reach it, and the path falls into the <c>Compile</c>.
    /// The result is the same; what changes is the cost, and that is why the shortcut exists.
    /// </summary>
    [Fact]
    public void Translate_APropertyOnAMethodResult_FallsBackToCompiling()
    {
        Expression<Func<Produto, bool>> expr = p => p.Categoria == CriarFiltro().Categoria;

        Assert.Equal("Produto[Categoria] = \"Vinda de método\"", TranslateToDax(expr.Body));
    }

    [Fact]
    public void Translate_AStaticProperty_IsEvaluated()
    {
        Expression<Func<Produto, bool>> expr = p => p.Categoria == string.Empty;

        Assert.Equal("Produto[Categoria] = \"\"", TranslateToDax(expr.Body));
    }
}
