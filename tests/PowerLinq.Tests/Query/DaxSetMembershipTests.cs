using System.Linq.Expressions;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Syntax;
using PowerLinq.DaxConverter.Translators;

namespace PowerLinq.Tests.Query;

/// <summary>
/// <c>colecao.Contains(coluna)</c> translates into DAX's set constructor,
/// <c>coluna IN { ... }</c> — the multi-select filter, which previously had no translation.
/// </summary>
/// <remarks>
/// Before this translation the case did not throw: the <c>string.Contains</c> path swallowed the
/// collection and generated
/// <c>SEARCH(Produto[Id], "System.Collections.Generic.List`1[System.Int32]", 1, 0) &gt; 0</c>,
/// syntactically valid DAX that looks for the column's value inside the collection's <b>type
/// name</b> and therefore never matches. The filter silently returned zero rows.
/// </remarks>
public sealed class DaxSetMembershipTests
{
    private enum Situacao
    {
        Pendente = 0,
        Aprovado = 1
    }

    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("Produto[Ativo]")] public bool Ativo { get; set; }
        [DaxColumn("Produto[Data]")] public DateTime Data { get; set; }
        [DaxColumn("Produto[Situacao]")] public Situacao Situacao { get; set; }
    }

    private static string Translate(Expression body) =>
        new DaxExpressionVisitor("Produto").Translate(body).ToDaxString();

    // ---------- the collection shapes ----------

    [Fact]
    public void List_BecomesIn()
    {
        var ids = new List<int> { 1, 2, 3 };
        Expression<Func<Produto, bool>> predicate = p => ids.Contains(p.Id);

        Assert.Equal("Produto[Id] IN { 1, 2, 3 }", Translate(predicate.Body));
    }

    [Fact]
    public void Array_BecomesIn()
    {
        // array.Contains(x) resolves to MemoryExtensions.Contains over ReadOnlySpan, not to
        // Enumerable.Contains — a different signature, the same intent.
        int[] ids = [4, 5];
        Expression<Func<Produto, bool>> predicate = p => ids.Contains(p.Id);

        Assert.Equal("Produto[Id] IN { 4, 5 }", Translate(predicate.Body));
    }

    [Fact]
    public void HashSet_BecomesIn()
    {
        var ids = new HashSet<int> { 7 };
        Expression<Func<Produto, bool>> predicate = p => ids.Contains(p.Id);

        Assert.Equal("Produto[Id] IN { 7 }", Translate(predicate.Body));
    }

    [Fact]
    public void EnumerableInterface_BecomesIn()
    {
        IEnumerable<int> ids = new List<int> { 8, 9 };
        Expression<Func<Produto, bool>> predicate = p => ids.Contains(p.Id);

        Assert.Equal("Produto[Id] IN { 8, 9 }", Translate(predicate.Body));
    }

    [Fact]
    public void LazyEnumerable_IsMaterialized()
    {
        // A lazy sequence is evaluated at translation time, like any closed expression.
        IEnumerable<int> ids = Enumerable.Range(1, 3).Where(value => value != 2);
        Expression<Func<Produto, bool>> predicate = p => ids.Contains(p.Id);

        Assert.Equal("Produto[Id] IN { 1, 3 }", Translate(predicate.Body));
    }

    // ---------- the values' types ----------

    [Fact]
    public void StringCollection_QuotesEachValue()
    {
        var nomes = new List<string> { "Eletrônicos", "Móveis" };
        Expression<Func<Produto, bool>> predicate = p => nomes.Contains(p.Nome);

        Assert.Equal("Produto[Nome] IN { \"Eletrônicos\", \"Móveis\" }", Translate(predicate.Body));
    }

    [Fact]
    public void StringCollection_EscapesEmbeddedQuotes()
    {
        var nomes = new List<string> { "O\"Brien" };
        Expression<Func<Produto, bool>> predicate = p => nomes.Contains(p.Nome);

        Assert.Equal("Produto[Nome] IN { \"O\"\"Brien\" }", Translate(predicate.Body));
    }

    [Fact]
    public void DateCollection_BecomesDateLiterals()
    {
        var datas = new List<DateTime> { new(2024, 1, 1), new(2024, 12, 31) };
        Expression<Func<Produto, bool>> predicate = p => datas.Contains(p.Data);

        Assert.Equal("Produto[Data] IN { DATE(2024,1,1), DATE(2024,12,31) }", Translate(predicate.Body));
    }

    [Fact]
    public void EnumCollection_BecomesNumericLiterals()
    {
        var situacoes = new List<Situacao> { Situacao.Pendente, Situacao.Aprovado };
        Expression<Func<Produto, bool>> predicate = p => situacoes.Contains(p.Situacao);

        Assert.Equal("Produto[Situacao] IN { 0, 1 }", Translate(predicate.Body));
    }

    [Fact]
    public void DuplicatesArePreservedBecauseTheyAreHarmless()
    {
        var ids = new List<int> { 1, 1, 2 };
        Expression<Func<Produto, bool>> predicate = p => ids.Contains(p.Id);

        // Deduplicating would be work with no gain: the engine handles it, and preserving keeps
        // the translation faithful to what was written.
        Assert.Equal("Produto[Id] IN { 1, 1, 2 }", Translate(predicate.Body));
    }

    // ---------- empty and null set ----------

    [Fact]
    public void EmptyCollection_BecomesAConstantFalse()
    {
        // `IN { }` is a syntax error in DAX, and an empty set matches nothing.
        var ids = new List<int>();
        Expression<Func<Produto, bool>> predicate = p => ids.Contains(p.Id);

        Assert.Equal("FALSE", Translate(predicate.Body));
    }

    [Fact]
    public void NegatedEmptyCollection_BecomesAlwaysTrue()
    {
        // It comes free from the false literal: NOT(FALSE).
        var ids = new List<int>();
        Expression<Func<Produto, bool>> predicate = p => !ids.Contains(p.Id);

        Assert.Equal("NOT(FALSE)", Translate(predicate.Body));
    }

    [Fact]
    public void NullCollection_ThrowsWithAClearMessage()
    {
        List<int>? ids = null;
        Expression<Func<Produto, bool>> predicate = p => ids!.Contains(p.Id);

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Translate(predicate.Body));

        Assert.Contains("is null", ex.Message);
        Assert.Contains("empty collection", ex.Message);
    }

    [Fact]
    public void NullCollectionMessage_IsLocalized()
    {
        IPowerLinqLocalizer portuguese = new ResourceManagerPowerLinqLocalizer("pt-BR");

        Assert.Contains("é nula", portuguese.Get("SetMembershipCollectionNull"));
    }

    // ---------- negation and composition ----------

    [Fact]
    public void Negated_WrapsInNot()
    {
        var ids = new List<int> { 1, 2 };
        Expression<Func<Produto, bool>> predicate = p => !ids.Contains(p.Id);

        Assert.Equal("NOT(Produto[Id] IN { 1, 2 })", Translate(predicate.Body));
    }

    [Fact]
    public void CombinedWithAnd_NeedsNoParentheses()
    {
        // IN has comparison precedence, above &&, so it is not parenthesized.
        var ids = new List<int> { 1 };
        Expression<Func<Produto, bool>> predicate = p => ids.Contains(p.Id) && p.Ativo;

        Assert.Equal("Produto[Id] IN { 1 } && Produto[Ativo]", Translate(predicate.Body));
    }

    [Fact]
    public void CombinedWithOrInsideAnd_KeepsTheGrouping()
    {
        var ids = new List<int> { 1 };
        Expression<Func<Produto, bool>> predicate = p => (ids.Contains(p.Id) || p.Ativo) && p.Id > 0;

        Assert.Equal(
            "(Produto[Id] IN { 1 } || Produto[Ativo]) && Produto[Id] > 0",
            Translate(predicate.Body));
    }

    [Fact]
    public void TwoSetMemberships_Compose()
    {
        var ids = new List<int> { 1 };
        var nomes = new List<string> { "A" };
        Expression<Func<Produto, bool>> predicate = p => ids.Contains(p.Id) && nomes.Contains(p.Nome);

        Assert.Equal(
            "Produto[Id] IN { 1 } && Produto[Nome] IN { \"A\" }",
            Translate(predicate.Body));
    }

    // ---------- what must not become IN ----------

    [Fact]
    public void StringContainsWithLiteral_StaysSearch()
    {
        // string is IEnumerable<char>, so it has to be excluded explicitly.
        Expression<Func<Produto, bool>> predicate = p => p.Nome.Contains("abc");

        Assert.Equal("SEARCH(\"abc\", Produto[Nome], 1, 0) > 0", Translate(predicate.Body));
    }

    [Fact]
    public void StringContainsWithCapturedValue_StaysSearch()
    {
        string trecho = "abc";
        Expression<Func<Produto, bool>> predicate = p => p.Nome.Contains(trecho);

        Assert.Equal("SEARCH(\"abc\", Produto[Nome], 1, 0) > 0", Translate(predicate.Body));
    }

    [Fact]
    public void ContainsOverAConstantValue_IsFoldedNotTranslated()
    {
        // Nothing here depends on the row, so the whole call is constant and resolves at
        // translation time — behavior inherited from the closed-subexpression fold.
        var ids = new List<int> { 1, 2 };
        Expression<Func<Produto, bool>> predicate = p => ids.Contains(2);

        Assert.Equal("TRUE", Translate(predicate.Body));
    }

    [Fact]
    public void ColumnContainsColumn_IsNotSetMembership()
    {
        // Both operands depend on the parameter: this is not membership in a constant set, and it
        // goes through the text path.
        Expression<Func<Produto, bool>> predicate = p => p.Nome.Contains(p.Nome);

        Assert.Equal("SEARCH(Produto[Nome], Produto[Nome], 1, 0) > 0", Translate(predicate.Body));
    }
}
