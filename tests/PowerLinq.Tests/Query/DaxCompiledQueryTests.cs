using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.Tests.Query;

/// <summary>
/// A compiled query: the DAX produced once, with the values bound on every execution.
/// </summary>
/// <remarks>
/// <para>
/// Measurement showed where the cost of producing DAX lies and pointed at two ways out. The chosen
/// one is a third: the parameter is <b>declared</b>, so the captured constant has no way into the
/// query's identity — which is the correctness bug named as the central risk of caching
/// translation.
/// </para>
/// <para>
/// The test that carries that guarantee is
/// <see cref="TwoValuesOnTheSameCompiledQuery_ProduceDifferentDax"/>: it would fail if the value
/// were baked into the template, which is the shape the bug would take.
/// </para>
/// </remarks>
public sealed class DaxCompiledQueryTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("Produto[Categoria]")] public string Categoria { get; set; } = "";
        [DaxColumn("Produto[Preco]")] public decimal Preco { get; set; }
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

    private static IDaxTable<Produto> Table() => new DaxTable<Produto>(new NoopExecutor());

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    // ---------- the guarantee ----------

    /// <summary>
    /// <b>It is the test asked for by name</b> — "a correct cache key in the presence of a
    /// captured variable, with a test that would fail if the constants entered the identity".
    /// </summary>
    /// <remarks>
    /// The same compiled query, two values, two DAX texts. If the value were baked into the
    /// template on the first binding — which is the exact shape the bug would take — the second
    /// would return <c>"Eletrônicos"</c> and this test would fail. It is the suite's only assertion
    /// that separates the correct implementation from the optimization that returns another execution's filter.
    /// </remarks>
    [Fact]
    public void TwoValuesOnTheSameCompiledQuery_ProduceDifferentDax()
    {
        DaxCompiledQuery<Produto, string> porCategoria = DaxCompiledQuery.Create<Produto, string>(
            (tabela, categoria) => tabela.Where(p => p.Categoria == categoria));

        IDaxTable<Produto> table = Table();

        string primeiro = Flat(porCategoria.ToDaxString(table, "Eletrônicos"));
        string segundo = Flat(porCategoria.ToDaxString(table, "Móveis"));

        Assert.Equal(
            "EVALUATE FILTER(Produto, Produto[Categoria] = \"Eletrônicos\")", primeiro);

        Assert.Equal(
            "EVALUATE FILTER(Produto, Produto[Categoria] = \"Móveis\")", segundo);
    }

    /// <summary>
    /// And the template is built <b>once</b>: that is what makes this an optimization rather than
    /// an indirection. Instance identity proves the reuse without depending on timing.
    /// </summary>
    [Fact]
    public void TheTemplate_IsBuiltOnceAndReused()
    {
        DaxCompiledQuery<Produto, string> porCategoria = DaxCompiledQuery.Create<Produto, string>(
            (tabela, categoria) => tabela.Where(p => p.Categoria == categoria));

        IDaxTable<Produto> table = Table();

        DaxTemplate primeiro = porCategoria.TemplateFor(table);
        porCategoria.ToDaxString(table, "Eletrônicos");
        DaxTemplate segundo = porCategoria.TemplateFor(table);

        Assert.Same(primeiro, segundo);
    }

    /// <summary>
    /// The template carries no value at all: the chunks are only the constant text, and the
    /// parameter's place is a slot. That is what makes keeping it safe.
    /// </summary>
    [Fact]
    public void TheTemplate_CarriesNoValue()
    {
        DaxTemplate template = DaxCompiledQuery
            .Create<Produto, string>((tabela, categoria) => tabela.Where(p => p.Categoria == categoria))
            .TemplateFor(Table());

        Assert.Equal(1, template.ParameterCount);
        Assert.Equal([0], template.Slots);
        Assert.Equal(2, template.Segments.Count);
        Assert.All(template.Segments, segmento =>
            Assert.DoesNotContain("Eletrônicos", segmento, StringComparison.Ordinal));
    }

    // ---------- where the parameter arrives ----------

    /// <summary>
    /// The explicit <c>parameter.Value</c> form translates the same as the implicit conversion.
    /// Covering both is not fussiness: whichever was left out would fall into constant folding, and
    /// since the marker throws on purpose, the symptom would be an obscure exception in the parameter's place.
    /// </summary>
    [Fact]
    public void TheExplicitValueForm_TranslatesTheSame()
    {
        string implicita = DaxCompiledQuery
            .Create<Produto, string>((t, categoria) => t.Where(p => p.Categoria == categoria))
            .ToDaxString(Table(), "Eletrônicos");

        string explicita = DaxCompiledQuery
            .Create<Produto, string>((t, categoria) => t.Where(p => p.Categoria == categoria.Value))
            .ToDaxString(Table(), "Eletrônicos");

        Assert.Equal(Flat(implicita), Flat(explicita));
    }

    /// <summary>Two parameters, each in its own slot, and the values' order is the declaration's.</summary>
    [Fact]
    public void TwoParameters_BindByPosition()
    {
        DaxCompiledQuery<Produto, string, decimal> consulta =
            DaxCompiledQuery.Create<Produto, string, decimal>(
                (tabela, categoria, minimo) => tabela
                    .Where(p => p.Categoria == categoria)
                    .Where(p => p.Preco > minimo));

        string dax = Flat(consulta.ToDaxString(Table(), "Móveis", 99.5m));

        Assert.Equal(
            "EVALUATE FILTER(Produto, Produto[Categoria] = \"Móveis\" && Produto[Preco] > 99.5)",
            dax);
    }

    /// <summary>
    /// A parameter that appears <b>twice</b> in the text is still a single value. Ordering by an
    /// expression goes into the <c>TOPN</c>'s argument and into the <c>ORDER BY</c> clause, so this
    /// is not a contrived case — it is what ordering by an expression made common.
    /// </summary>
    [Fact]
    public void AParameterWrittenTwice_IsStillOneValue()
    {
        DaxCompiledQuery<Produto, decimal> consulta = DaxCompiledQuery.Create<Produto, decimal>(
            (tabela, fator) => tabela.OrderByDescending(p => p.Preco * fator).Take(5));

        DaxTemplate template = consulta.TemplateFor(Table());

        Assert.Equal(1, template.ParameterCount);
        Assert.Equal([0, 0], template.Slots);

        Assert.Equal(
            "EVALUATE TOPN(5, Produto, Produto[Preco] * 2, DESC) "
            + "ORDER BY Produto[Preco] * 2 DESC",
            Flat(consulta.ToDaxString(Table(), 2m)));
    }

    /// <summary>
    /// The value's type decides the literal's syntax, which is why it is chosen at <b>binding</b>
    /// time — the same slot receiving <see langword="null"/> becomes <c>BLANK()</c>, not an empty string.
    /// </summary>
    [Fact]
    public void TheLiteralSyntax_ComesFromTheBoundValue()
    {
        DaxCompiledQuery<Produto, string?> consulta = DaxCompiledQuery.Create<Produto, string?>(
            (tabela, nome) => tabela.Where(p => p.Nome == nome));

        IDaxTable<Produto> table = Table();

        Assert.Contains("= \"x\"", consulta.ToDaxString(table, "x"), StringComparison.Ordinal);
        Assert.Contains("= BLANK()", consulta.ToDaxString(table, null), StringComparison.Ordinal);

        // Quotes are still escaped by the literal node, and not by text concatenation here.
        Assert.Contains(
            "= \"a\"\"b\"", consulta.ToDaxString(table, "a\"b"), StringComparison.Ordinal);
    }

    // ---------- what fails loudly ----------

    /// <summary>
    /// <b>The central property.</b> The marker has no value, and reading its value throws — so no
    /// path the translation does not recognize can bake a value into the template silently.
    /// Failing is the correct behaviour: the silent alternative is the bug.
    /// </summary>
    [Fact]
    public void ReadingTheParameterValueOutsideATranslation_Throws()
    {
        var parametro = new DaxParameter<string>(0);

        InvalidOperationException porValue =
            Assert.Throws<InvalidOperationException>(() => parametro.Value);

        InvalidOperationException porConversao =
            Assert.Throws<InvalidOperationException>(() => (string)parametro);

        foreach (InvalidOperationException ex in new[] { porValue, porConversao })
            Assert.Contains("has no value", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tree with a parameter does not have <b>one</b> text, and the ordinary query's
    /// <c>ToDaxString</c> refuses rather than returning DAX with the parameter's place empty —
    /// invalid syntax handed over as if it were a query.
    /// </summary>
    [Fact]
    public void RenderingAParameterizedTreeAsText_IsRefused()
    {
        var parametro = new DaxParameter<string>(0);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Table().Where(p => p.Categoria == parametro).ToDaxString());

        Assert.Contains("RenderTemplate", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A declared and unused parameter is an error. Without this the call would ask for a value
    /// that goes nowhere, and whoever reads the call would believe it filters.
    /// </summary>
    [Fact]
    public void ADeclaredParameterThatNeverReachesTheDax_IsRefused()
    {
        DaxCompiledQuery<Produto, string> consulta = DaxCompiledQuery.Create<Produto, string>(
            (tabela, _) => tabela.Where(p => p.Categoria == "fixo"));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => consulta.ToDaxString(Table(), "ignorado"));

        Assert.Contains("declares 1 parameter", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Binding the wrong number of values is refused by the template, and not silently completed:
    /// too few would leave the parameter's place empty.
    /// </summary>
    [Fact]
    public void BindingTheWrongNumberOfValues_IsRefused()
    {
        DaxTemplate template = DaxCompiledQuery
            .Create<Produto, string>((t, categoria) => t.Where(p => p.Categoria == categoria))
            .TemplateFor(Table());

        ArgumentException ex = Assert.Throws<ArgumentException>(() => template.Bind([]));

        Assert.Contains("1 parameter value(s)", ex.Message, StringComparison.Ordinal);
    }

    // ---------- coexistence ----------

    /// <summary>
    /// A query <b>without</b> any parameter goes through the same path: a one-chunk template, zero
    /// slots, and the usual DAX. There is no special case in the writing.
    /// </summary>
    [Fact]
    public void ATreeWithoutParameters_RendersTheSameThroughTheTemplate()
    {
        DaxQuery<Produto> consulta = Table().Where(p => p.Categoria == "Eletrônicos");

        DaxTemplate template = DaxWriter.RenderTemplate(consulta.ToSyntaxTree());

        Assert.Equal(0, template.ParameterCount);
        Assert.Single(template.Segments);
        Assert.Equal(consulta.ToDaxString(), template.Bind([]));
    }
}
