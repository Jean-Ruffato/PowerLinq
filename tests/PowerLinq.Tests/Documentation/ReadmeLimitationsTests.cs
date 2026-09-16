using System.Reflection;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Context;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Linq;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Documentation;

/// <summary>
/// Mirrors the root README's <b>Limitações</b> section and the <c>DaxConverter</c> README's
/// <b>Limitações atuais</b> section: one test per documented limitation.
/// </summary>
/// <remarks>
/// <para>
/// The goal is not behaviour coverage — most of these cases already have a test in another file,
/// along the axis of what the implementation does. Here the axis is different: <b>the
/// documentation becomes CI-verified</b>, instead of relying on the memory of whoever wrote it.
/// Each test quotes the claim it mirrors, so when a limitation stops existing it is this file that
/// fails, and whoever implements the translation is forced to update the README in the same pass.
/// </para>
/// <para>
/// That this matters is not hypothetical: the list of limitations gave three examples of
/// constructions that "must fail" — <c>new DateTime(2024,1,1)</c>, <c>p.Nome.Length</c> and
/// <c>p.Nome.Substring(0,1)</c> — and <b>all three started working</b>.
/// The list aged before anyone reviewed it, which is exactly what it predicted.
/// </para>
/// <para>
/// Each case asserts the exception's <b>type</b> <b>and</b> something of the message. The type
/// alone would not be enough: if the exception started coming from somewhere else — such as the
/// accidental <c>InvalidOperationException</c>, already fixed, leaked from <c>Expression.Compile()</c>
/// — the test would stay green while asserting a contract that does not exist.
/// </para>
/// </remarks>
public sealed class ReadmeLimitationsTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("Produto[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("Produto[Preco]")] public decimal Preco { get; set; }
        [DaxColumn("Produto[Ativo]")] public bool Ativo { get; set; }
        [DaxColumn("Produto[Data]")] public DateTime Data { get; set; }
    }

    private sealed class Projetado
    {
        [DaxColumn("Produto[Categoria]")] public string Categoria { get; set; } = "";
        public decimal Total { get; set; }
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

    private static DaxTable<Produto> Tabela() => new(new NoopExecutor());

    // =====================================================================================
    // Root README — "The operator order is fixed"
    //
    //   tabela.Take(5).Where(p => p.Ativo)      // NotSupportedException
    //   tabela.Take(5).OrderBy(p => p.Nome)     // NotSupportedException
    //   tabela.Take(5).GroupBy(p => p.Nome)     // NotSupportedException
    //   tabela.OrderBy(p => p.Id).Take(5).Skip(10)  // NotSupportedException
    // =====================================================================================

    [Fact]
    public void WhereAfterTake_FiltersTheWindow()
    {
        // It was a limitation, and the README was updated in the same pass — see the cycle
        // described in CONTRIBUTING: a limitation test becomes a behaviour test.
        string dax = Tabela().Take(5).Where(p => p.Ativo).ToDaxString();

        Assert.Contains("FILTER(", dax);
        Assert.Contains("TOPN(", dax);
        // The FILTER wraps the TOPN, not the other way round: it filters the window, not the table.
        Assert.True(
            dax.IndexOf("FILTER(", StringComparison.Ordinal)
                < dax.IndexOf("TOPN(", StringComparison.Ordinal),
            "o FILTER precisa envolver o TOPN");
    }

    [Fact]
    public void OrderByAfterTake_OrdersTheWindow()
    {
        string dax = Tabela().Take(5).OrderBy(p => p.Nome).ToDaxString();

        Assert.Contains("TOPN(", dax);
        Assert.Contains("ORDER BY", dax);
    }

    [Fact]
    public void GroupByAfterTake_IsRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Tabela().Take(5).GroupBy(p => p.Nome));

        Assert.Contains("GroupBy", ex.Message);
        Assert.Contains("after Take", ex.Message);
    }

    [Fact]
    public void SkipAfterTake_PagesTheWindow()
    {
        // It required the ordering redeclared while Take consumed the pending terms. The fix
        // stopped consuming them — because TOPN does not guarantee the output's order and the
        // EVALUATE's ORDER BY needs them — and so the following Skip finds the ordering in force.
        string dax = Tabela().OrderBy(p => p.Id).Take(5).Skip(10).ToDaxString();

        Assert.Contains("EXCEPT(", dax);
        Assert.Contains("TOPN(", dax);
    }

    /// <summary>
    /// The line marked <c>// ok</c> in the README. Without it, a test that only checks refusals
    /// would pass even if the canonical order broke — and the README would be right about what
    /// fails and wrong about what works.
    /// </summary>
    [Fact]
    public void TheCanonicalOrder_Works()
    {
        string dax = Tabela()
            .Where(p => p.Ativo)
            .OrderBy(p => p.Id)
            .Skip(10)
            .Take(5)
            .ToDaxString();

        Assert.Contains("TOPN(", dax);
        Assert.Contains("EXCEPT(", dax);
    }

    /// <summary>
    /// README: "<c>Select</c> <b>is</b> allowed after <c>Take</c>, because
    /// <c>SELECTCOLUMNS(TOPN(...))</c> matches LINQ's semantics."
    /// </summary>
    [Fact]
    public void SelectAfterTake_IsAllowed()
    {
        // The ordering column has to be carried by the projection, otherwise the EVALUATE's ORDER
        // BY would reference a column the result does not have.
        string dax = Tabela()
            .OrderBy(p => p.Categoria)
            .Take(5)
            .Select(p => new Projetado { Categoria = p.Categoria })
            .ToDaxString();

        Assert.Contains("SELECTCOLUMNS(", dax);
        Assert.Contains("TOPN(", dax);
    }

    /// <summary>
    /// README: "<c>WhereIf</c> with a false condition passes too, since it composes nothing."
    /// </summary>
    [Fact]
    public void WhereIfWithFalseCondition_PassesAfterTake()
    {
        DaxQuery<Produto> consulta = Tabela().Take(5).WhereIf(false, p => p.Ativo);

        Assert.Contains("TOPN(", consulta.ToDaxString());
    }

    // =====================================================================================
    // Root README — "Ordering an aggregated result only works by a key column"
    // =====================================================================================

    [Fact]
    public void OrderByAGroupingKey_ReachesTheDax()
    {
        string dax = Tabela()
            .OrderBy(p => p.Categoria)
            .GroupBy(p => p.Categoria)
            .Select(g => new Projetado { Categoria = g.Key, Total = g.Sum(p => p.Preco) })
            .ToDaxString();

        Assert.Contains("ORDER BY Produto[Categoria] ASC", dax);
    }

    [Fact]
    public void OrderByANonKeyColumn_IsRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => Tabela()
            .OrderByDescending(p => p.Preco)
            .GroupBy(p => p.Categoria)
            .Select(g => new Projetado { Categoria = g.Key, Total = g.Sum(p => p.Preco) })
            .ToDaxString());

        Assert.Contains("not a grouping key", ex.Message);
        Assert.Contains("Produto[Preco]", ex.Message);
    }

    // =====================================================================================
    // Root README — "The context does not track changes"
    //
    // "there is no change tracking, no SaveChanges and no write commands"
    // =====================================================================================

    /// <summary>
    /// It checks for absence, not presence. It is the only way to test this claim: if someone adds
    /// a <c>SaveChanges</c> to the context, the README starts lying and nothing else would catch it.
    /// </summary>
    [Fact]
    public void TheContextExposesNoWriteApi()
    {
        string[] escrita = ["SaveChanges", "SaveChangesAsync", "Add", "Update", "Remove", "Delete", "Insert"];

        IEnumerable<string> encontrados = typeof(DaxContext)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(member => member.Name)
            .Where(nome => escrita.Any(proibido =>
                nome.StartsWith(proibido, StringComparison.OrdinalIgnoreCase)));

        Assert.Empty(encontrados);
    }

    // =====================================================================================
    // Root README — "Immutable contracts"
    //
    // This WAS the limitation "Projections use object initializers", which said the result
    // contract needed a public parameterless constructor. The two tests stay here to stop
    // the documentation from describing it again.
    // =====================================================================================

    private sealed record ProjetadoImutavel(
        [property: DaxColumn("Produto[Categoria]")] string Categoria, decimal Total);

    [Fact]
    public void AConstructorProjection_IsNoLongerRefused()
    {
        string dax = Tabela()
            .Select(p => new ProjetadoImutavel(p.Categoria, p.Preco))
            .ToDaxString();

        Assert.Contains("\"Categoria\", Produto[Categoria]", dax, StringComparison.Ordinal);
        Assert.Contains("\"Total\", Produto[Preco]", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// What is still refused is the <b>empty</b> projection: a one-argument <c>SELECTCOLUMNS</c>,
    /// which the server rejects, and which is almost always a mistake by whoever wrote it.
    /// </summary>
    [Fact]
    public void AnEmptyProjection_IsRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Tabela().Select(p => new Projetado()).ToDaxString());

        Assert.Contains("object initializer", ex.Message);
    }

    // =====================================================================================
    // Root README — "Nested members and some methods are not translated"
    //
    // What does NOT depend on the parameter is evaluated during translation and becomes a literal;
    // what does depend has to have a translation, or is refused NAMING the member.
    // =====================================================================================

    [Theory]
    // The three cases the README lists as refused, with the member the message has to name.
    [InlineData("DayOfYear")]
    [InlineData("Ticks")]
    public void ANestedMemberWithoutTranslation_IsRefusedNamingThePath(string membro)
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => (membro switch
        {
            "DayOfYear" => Tabela().Where(p => p.Data.DayOfYear > 3),
            _ => Tabela().Where(p => p.Data.Ticks > 0)
        }).ToDaxString());

        // The message names the path, and that is what distinguishes it from the accidental
        // InvalidOperationException that has already been fixed.
        Assert.Contains($"Produto.Data.{membro}", ex.Message);
        Assert.Contains("no DAX translation", ex.Message);
    }

    [Fact]
    public void AMethodWithoutTranslation_IsRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Tabela().Where(p => p.Nome.PadLeft(5) == "A").ToDaxString());

        Assert.Contains("PadLeft", ex.Message);
    }

    /// <summary>
    /// The other side of the same rule, and what the list predicted would age: these three
    /// constructions <b>were</b> documented as refused and work today. Pinning them here is what
    /// stops the documentation from describing them as a limitation again.
    /// </summary>
    [Fact]
    public void WhatDoesNotDependOnTheParameter_IsEvaluatedDuringTranslation()
    {
        Assert.Contains("DATE(2024,1,1)", Tabela().Where(p => p.Data >= new DateTime(2024, 1, 1)).ToDaxString());

        string comEstatica = Tabela().Where(p => p.Data >= DateTime.Today.AddDays(-30)).ToDaxString();
        Assert.Contains("DATE(", comEstatica);

        string texto = "10.5";
        Assert.Contains("10.5", Tabela().Where(p => p.Preco > decimal.Parse(texto,
            System.Globalization.CultureInfo.InvariantCulture)).ToDaxString());

        var inicio = new DateTime(2024, 1, 1);
        Assert.Contains("DATE(2024,1,1)", Tabela().Where(p => p.Data >= inicio).ToDaxString());
    }

    /// <summary>
    /// README: "<c>text.Length</c> → <c>LEN(text)</c>" and "<c>Substring</c> → <c>MID</c>". They
    /// were a limitation when the list was written; today they are a feature.
    /// </summary>
    [Fact]
    public void WhatUsedToBeALimitationInIssue38_NowTranslates()
    {
        Assert.Contains("LEN(Produto[Nome])", Tabela().Where(p => p.Nome.Length > 3).ToDaxString());
        Assert.Contains("MID(Produto[Nome], 1, 1)", Tabela().Where(p => p.Nome.Substring(0, 1) == "A").ToDaxString());
    }

    // =====================================================================================
    // DaxConverter README — "Current limitations"
    //
    // "Skip requires an OrderBy, since pagination without order is not deterministic."
    // =====================================================================================

    [Fact]
    public void SkipWithoutOrderBy_IsRefused()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Tabela().Skip(10).ToDaxString());

        Assert.Contains("Skip requires an OrderBy", ex.Message);
    }

    // =====================================================================================
    // Messages in both languages of the .resx
    //
    // A key present only in English works silently under pt-BR — the ResourceManager falls back
    // and nobody notices until someone reads the message in the wrong language.
    // =====================================================================================

    [Theory]
    [InlineData("OperatorAfterTake", "não pode ser aplicado depois de Take")]
    [InlineData("OrderByColumnNotGrouped", "não é chave do agrupamento")]
    [InlineData("SkipRequiresOrderBy", "Skip requer um OrderBy")]
    [InlineData("SelectInitializerRequired", "inicializador de objeto")]
    [InlineData("MemberNotTranslatable", "não tem tradução para DAX")]
    [InlineData("MethodUnsupported", "não é suportado na tradução")]
    [InlineData("TryParseUnsupported", "não tem tradução, e Parse é a forma a usar")]
    public void EveryLimitationMessage_ExistsInPortuguese(string chave, string esperado)
    {
        IPowerLinqLocalizer portugues = new ResourceManagerPowerLinqLocalizer("pt-BR");

        Assert.Contains(esperado, portugues.Get(chave));
    }

    // =====================================================================================
    // Root README — "int.TryParse is refused with a message of its own"
    // =====================================================================================

    /// <summary>
    /// Destination of the <c>out</c>. A field because the two forms that compile inside an
    /// expression tree are an already-declared variable and a field — both have to exist for the test to cover both.
    /// </summary>
    private static int _parsed;

    /// <summary>
    /// README: "<c>int.TryParse</c> is <b>refused with a message of its own</b>, and not with the
    /// generic untranslatable-method one."
    /// </summary>
    /// <remarks>
    /// This is the limitation the README stated <b>wrongly</b> — it said <c>TryParse</c> never
    /// reached the translator, because the compiler refuses <c>out</c> inside an expression tree.
    /// It refuses the inline declaration (CS8198) and the discard (CS8207); an <c>out</c> over an
    /// already-declared variable or over a field compiles and gets through. It was the only README
    /// limitation with no test here, which is why the false claim survived.
    /// </remarks>
    [Fact]
    public void TryParseOverAColumn_IsRefusedWithItsOwnMessage()
    {
        int local = 0;

        NotSupportedException porLocal = Assert.Throws<NotSupportedException>(
            () => Tabela().Where(p => int.TryParse(p.Nome, out local)).ToDaxString());

        NotSupportedException porCampo = Assert.Throws<NotSupportedException>(
            () => Tabela().Where(p => int.TryParse(p.Nome, out _parsed)).ToDaxString());

        foreach (NotSupportedException ex in new[] { porLocal, porCampo })
        {
            Assert.Contains("out parameter", ex.Message);
            Assert.Contains("not to throw", ex.Message);
            Assert.Contains("Int32.Parse", ex.Message);
            Assert.DoesNotContain("is not supported in DAX", ex.Message);
        }
    }

    /// <summary>
    /// README: "<c>DateTime.TryParse</c> still falls into the <b>generic</b> refusal."
    /// </summary>
    [Fact]
    public void TryParseOfATypeWithoutParseTranslation_KeepsTheGenericRefusal()
    {
        DateTime local = default;

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Tabela().Where(p => DateTime.TryParse(p.Nome, out local)).ToDaxString());

        Assert.Contains("is not supported in DAX", ex.Message);
    }

    /// <summary>
    /// README: "The <c>IQueryable</c> surface covers a subset of the operators. [...] including
    /// <c>GroupBy</c> and <c>Join</c>, which exist in the fluent API and stay only there."
    /// </summary>
    [Fact]
    public void GroupByOnTheQueryableSurface_IsRefusedPointingToTheFluentApi()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Tabela().AsQueryable().GroupBy(p => p.Categoria));

        Assert.Contains("GroupBy", ex.Message);
        Assert.Contains("fluent API", ex.Message);
    }

    /// <summary>
    /// README: "There is no synchronous enumeration on the <c>IQueryable</c>. [...] they throw
    /// <c>NotSupportedException</c> naming the asynchronous form."
    /// </summary>
    [Fact]
    public void SynchronousEnumerationOnTheQueryableSurface_IsRefused()
    {
        IQueryable<Produto> produtos = Tabela().AsQueryable();

        Assert.Contains(
            "ToListAsync", Assert.Throws<NotSupportedException>(() => produtos.ToList()).Message);

        Assert.Contains(
            "CountAsync", Assert.Throws<NotSupportedException>(() => produtos.Count()).Message);
    }

    [Theory]
    [InlineData("OperatorAfterTake")]
    [InlineData("OrderByColumnNotGrouped")]
    [InlineData("SkipRequiresOrderBy")]
    [InlineData("SelectInitializerRequired")]
    [InlineData("MemberNotTranslatable")]
    [InlineData("MethodUnsupported")]
    [InlineData("TryParseUnsupported")]
    [InlineData("QueryableOperatorUnsupported")]
    [InlineData("QueryableOverloadUnsupported")]
    [InlineData("QueryableSyncEnumeration")]
    [InlineData("QueryableScalarProjection")]
    public void NoLimitationMessage_FallsBackToTheKeyName(string chave)
    {
        // Get returns the KEY ITSELF when it finds nothing. A test that only checked "is not
        // empty" would pass with the raw key showing up in the user's face.
        foreach (string idioma in (string[])["en", "pt-BR"])
            Assert.NotEqual(chave, new ResourceManagerPowerLinqLocalizer(idioma).Get(chave));
    }
}
