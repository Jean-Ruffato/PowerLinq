using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Builders;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Ordering by an <b>expression</b>, and not only by a property.
/// </summary>
/// <remarks>
/// <para>
/// <c>DaxOrderTerm</c> carried a <c>DaxColumnRef</c>, so <c>OrderBy(x =&gt; expression)</c> was
/// refused with "must reference a single property". In DAX the destination never had that
/// restriction: both the <c>EVALUATE</c>'s <c>ORDER BY</c> and <c>TOPN</c>'s ordering arguments
/// accept an expression.
/// </para>
/// <para>
/// <b>The care is in the interaction with the reshape.</b> After a reshape the result carries a
/// closed set of columns, and the pending ordering is rewritten onto the output columns. The
/// earlier version matched by <b>column reference</b>, so it refused expressions wholesale; today
/// the rewrite walks the expression and swaps <b>each</b> reference for the corresponding output
/// column, refusing when one of them does not survive the reshape. There is still no guessing —
/// what changed is the granularity of the refusal.
/// </para>
/// </remarks>
public sealed class DaxOrderByExpressionTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("Produto[Preco]")] public decimal Preco { get; set; }
        [DaxColumn("Produto[Quantidade]")] public int Quantidade { get; set; }
        [DaxColumn("Produto[Ativo]")] public bool Ativo { get; set; }
    }

    private sealed class Projetado
    {
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
    }

    /// <summary>
    /// A projection contract whose output name is the result column's — <c>[Nome]</c> — and not the
    /// source's qualified reference. It is the shape in which the rewrite produces readable DAX.
    /// </summary>
    private sealed class Reduzido
    {
        [DaxColumn("Nome")] public string Nome { get; set; } = "";
    }

    private sealed class ReduzidoComFlag
    {
        [DaxColumn("Nome")] public string Nome { get; set; } = "";
        [DaxColumn("Ativo")] public bool Ativo { get; set; }
    }

    private sealed class Agrupado
    {
        [DaxColumn("Produto[Nome]")] public string Nome { get; set; } = "";
        [DaxColumn("[Total]")] public decimal Total { get; set; }
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

    private static DaxTable<Produto> Table() => new(new NoopExecutor());

    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    // ---------- what started working ----------

    [Fact]
    public void OrderingByAnArithmeticExpression_GoesToTheServer()
    {
        string dax = Flat(Table().OrderByDescending(p => p.Preco * p.Quantidade).ToDaxString());

        Assert.Equal(
            "EVALUATE Produto ORDER BY Produto[Preco] * Produto[Quantidade] DESC", dax);
    }

    [Fact]
    public void OrderingByATextFunction_GoesToTheServer()
    {
        string dax = Flat(Table().OrderBy(p => p.Nome.ToUpper()).ToDaxString());

        Assert.Equal("EVALUATE Produto ORDER BY UPPER(Produto[Nome]) ASC", dax);
    }

    /// <summary>
    /// <c>ThenBy</c> by expression too, coexisting with an <c>OrderBy</c> by property — both kinds
    /// of term in the same clause.
    /// </summary>
    [Fact]
    public void APropertyAndAnExpression_CoexistInTheSameClause()
    {
        string dax = Flat(Table()
            .OrderBy(p => p.Nome)
            .ThenByDescending(p => p.Preco * p.Quantidade)
            .ToDaxString());

        Assert.Equal(
            "EVALUATE Produto ORDER BY Produto[Nome] ASC, Produto[Preco] * Produto[Quantidade] DESC",
            dax);
    }

    /// <summary>
    /// <c>Skip</c> requires an ordering, and an expression serves: it goes as an argument of the
    /// <c>TOPN</c> inside the <c>EXCEPT</c>, which is where pagination uses it.
    /// </summary>
    [Fact]
    public void PagingByAnExpression_Works()
    {
        string dax = Flat(Table().OrderBy(p => p.Preco * p.Quantidade).Skip(10).ToDaxString());

        Assert.Equal(
            "EVALUATE EXCEPT(Produto, "
            + "TOPN(10, Produto, Produto[Preco] * Produto[Quantidade], ASC)) "
            + "ORDER BY Produto[Preco] * Produto[Quantidade] ASC",
            dax);
    }

    // ---------- what a simple property still is ----------

    /// <summary>
    /// Ordering by a property still generates <b>exactly</b> the same DAX. The shape decides the
    /// path before translating, so no existing query changes.
    /// </summary>
    [Fact]
    public void OrderingByAProperty_IsUnchanged()
    {
        Assert.Equal(
            "EVALUATE Produto ORDER BY Produto[Nome] ASC",
            Flat(Table().OrderBy(p => p.Nome).ToDaxString()));
    }

    /// <summary>
    /// And a property the reshape left out still fails with the reshape's message, rather than
    /// slipping into the translator and failing with a different one.
    /// </summary>
    [Fact]
    public void APropertyDroppedByAReshape_StillFailsWithTheReshapeMessage()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table()
                .OrderBy(p => p.Preco)
                .Select(p => new Projetado { Nome = p.Nome })
                .ToDaxString());

        // It names the COLUMN, not "expression": the property path is still the old one.
        Assert.Contains("Produto[Preco]", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("expression", ex.Message, StringComparison.Ordinal);
    }

    // ---------- what the rewrite crosses ----------

    /// <summary>
    /// Ordering by an expression <b>after</b> a reshape holds when every column it references is in
    /// the result. Here the expression talks about the grouping's extension column, which the
    /// result carries — there is nothing to guess.
    /// </summary>
    [Fact]
    public void AnExpressionOverTheResultColumns_IsAcceptedAfterAReshape()
    {
        string dax = Flat(Table()
            .GroupBy(p => p.Nome)
            .Select(g => new Agrupado { Nome = g.Key, Total = g.Sum(p => p.Preco) })
            .OrderBy(a => a.Total * 2)
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SUMMARIZECOLUMNS(Produto[Nome], \"Total\", SUM(Produto[Preco])) "
            + "ORDER BY [Total] * 2 ASC",
            dax);
    }

    /// <summary>
    /// And ordering by an expression <b>before</b> a projection: the pending ordering is rewritten
    /// reference by reference onto the output columns — <c>Produto[Nome]</c> becomes <c>[Nome]</c>
    /// inside the <c>UPPER</c>.
    /// </summary>
    [Fact]
    public void AnExpressionOrderBeforeAProjection_IsRewrittenToTheOutputColumns()
    {
        string dax = Flat(Table()
            .OrderBy(p => p.Nome.ToUpper())
            .Select(p => new Reduzido { Nome = p.Nome })
            .ToDaxString());

        Assert.Equal(
            "EVALUATE SELECTCOLUMNS(Produto, \"Nome\", Produto[Nome]) ORDER BY UPPER([Nome]) ASC",
            dax);
    }

    /// <summary>
    /// The motivating case: the sort key is built from the text of a column —
    /// <c>year * 100 + month</c> from <c>MM-yyyy</c> — and the projection reduces the query to that
    /// column. All <b>four</b> references to the source column are rewritten.
    /// </summary>
    [Fact]
    public void ATextKeyOrder_SurvivesTheProjectionThatReducesToThatColumn()
    {
        string dax = Flat(Table()
            .OrderByDescending(p => (int.Parse(p.Nome.Substring(3)) * 100)
                                    + int.Parse(p.Nome.Substring(0, 2)))
            .Select(p => new Reduzido { Nome = p.Nome })
            .Distinct()
            .ToDaxString());

        Assert.Equal(
            "EVALUATE DISTINCT(SELECTCOLUMNS(Produto, \"Nome\", Produto[Nome])) "
            + "ORDER BY VALUE(MID([Nome], 4, LEN([Nome]))) * 100 "
            + "+ VALUE(MID([Nome], 1, 2)) DESC",
            dax);
    }

    /// <summary>
    /// The negation is crossed like any other node — ordering by an inverted flag is the use case,
    /// and the column inside the <c>NOT</c> is rewritten onto the output one.
    /// </summary>
    [Fact]
    public void ANegationInTheOrder_IsRewrittenThroughTheNot()
    {
        string dax = Flat(Table()
            .OrderBy(p => !p.Ativo)
            .Select(p => new ReduzidoComFlag { Nome = p.Nome, Ativo = p.Ativo })
            .ToDaxString());

        Assert.EndsWith("ORDER BY NOT([Ativo]) ASC", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// And set membership too: the tested value is rewritten, and the set's literals pass through
    /// untouched because they have no column inside them.
    /// </summary>
    [Fact]
    public void AMembershipTestInTheOrder_IsRewrittenThroughTheIn()
    {
        string[] destaques = ["Teclado", "Mouse"];

        string dax = Flat(Table()
            .OrderByDescending(p => destaques.Contains(p.Nome))
            .Select(p => new Reduzido { Nome = p.Nome })
            .ToDaxString());

        Assert.EndsWith(
            "ORDER BY [Nome] IN { \"Teclado\", \"Mouse\" } DESC", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exact shape a chart by period uses: group, sort by the key built from the text, cut the
    /// N most recent and re-sort them chronologically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both</b> orderings matter, and for different reasons. The descending one chooses which
    /// rows come in — it is an argument of the <c>TOPN</c>. The ascending one decides the order they
    /// come back in — DAX only guarantees that through the <c>EVALUATE</c>'s <c>ORDER BY</c> clause.
    /// Discarding the second gave the right N rows in an arbitrary order, which in practice usually
    /// comes out sorted: worse than coming out wrong, because it does not show up in a test.
    /// </para>
    /// <para>
    /// The key is the same expression in both places — declaring it once is what guarantees that.
    /// A <c>TOPN</c> and an <c>ORDER BY</c> disagreeing would pick one set and return another.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLatestNByATextKey_IsOneQuery()
    {
        string dax = Flat(Table()
            .GroupBy(p => p.Nome)
            .Select(g => new Agrupado { Nome = g.Key, Total = g.Sum(p => p.Preco) })
            .OrderByDescending(a => int.Parse(a.Nome.Substring(3)))
            .Take(12)
            .OrderBy(a => int.Parse(a.Nome.Substring(3)))
            .ToDaxString());

        Assert.Equal(
            "EVALUATE TOPN(12, "
            + "SUMMARIZECOLUMNS(Produto[Nome], \"Total\", SUM(Produto[Preco])), "
            + "VALUE(MID(Produto[Nome], 4, LEN(Produto[Nome]))), DESC) "
            + "ORDER BY VALUE(MID(Produto[Nome], 4, LEN(Produto[Nome]))) ASC",
            dax);
    }

    // ---------- what is refused ----------

    /// <summary>
    /// <b>One</b> reference the reshape does not carry forward is enough for the whole term to
    /// fall, and the message names the missing column — not "the expression". It is the same
    /// refusal as before, now applied to what is actually missing.
    /// </summary>
    [Fact]
    public void AnExpressionOverAColumnTheProjectionDrops_IsRefusedNamingThatColumn()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table()
                .OrderBy(p => p.Preco * p.Quantidade)
                .Select(p => new Projetado { Nome = p.Nome })
                .ToDaxString());

        Assert.Contains("Produto[Preco]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Select", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// In a <c>GroupBy</c> the ordering has to be by a key, and an expression is not a key. The
    /// aggregation collapses the group's rows, so there is no per-output-row value left for the
    /// expression to evaluate.
    /// </summary>
    [Fact]
    public void AnExpressionOrderBeforeAGroupBy_IsRefused()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => Table()
                .OrderBy(p => p.Preco * p.Quantidade)
                .GroupBy(p => p.Nome)
                .Select(g => new Agrupado { Nome = g.Key, Total = g.Sum(p => p.Preco) })
                .ToDaxString());

        Assert.Contains("Produto[Preco] * Produto[Quantidade]", ex.Message, StringComparison.Ordinal);
    }

    // ---------- the builder is public, and checks on its own ----------

    /// <summary>
    /// After a reshape, an expression that talks about a column outside the result falls with the
    /// reshape's message, which lists the closed set. Built by hand because fluent composition
    /// already resolves the ordering against the result before getting here — the builder's guard
    /// is for whoever builds the pipeline directly.
    /// </summary>
    [Fact]
    public void AHandBuiltPipeline_RefusesAnExpressionOverAColumnOutsideTheResult()
    {
        var pipeline = new DaxPipeline("Produto", typeof(object))
        {
            Stages =
            [
                new DaxProjectStage(
                    [new DaxProjectionColumn("Nome", new DaxColumnRef("Produto[Nome]"))]),
                Order(Times(new DaxColumnRef("Produto[Preco]"), 2)),
            ],
        };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => DaxPipelineBuilder.Build(pipeline));

        Assert.Contains("Produto[Preco]", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The traversal is closed by omission.</b> A measure is resolved by name against the model,
    /// not against the result's columns, so the rewrite does not cross it — and refuses instead of
    /// returning it untouched. It is what stops a new <c>Syntax</c> node from slipping through here
    /// untreated: the cost of forgetting is DAX that sorts by a column the result does not have.
    /// </summary>
    [Fact]
    public void AHandBuiltPipeline_RefusesANodeTheRewriteCannotTraverse()
    {
        var pipeline = new DaxPipeline("Produto", typeof(object))
        {
            Stages =
            [
                Order(Times(new DaxMeasureRef("[Total Vendas]"), 2)),
                new DaxProjectStage(
                    [new DaxProjectionColumn("Nome", new DaxColumnRef("Produto[Nome]"))]),
            ],
        };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => DaxPipelineBuilder.Build(pipeline));

        Assert.Contains("[Total Vendas]", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scalar function accepts a <b>table</b> as an argument — <c>COUNTROWS(FILTER(...))</c> is
    /// the case — and there the rewrite stops. The argument talks about a model table, whose shape
    /// the reshape did not change: swapping its columns would be wrong, and leaving it untouched
    /// would sort by a subquery nobody checked.
    /// </summary>
    [Fact]
    public void AHandBuiltPipeline_RefusesATableArgumentInsideTheOrderingExpression()
    {
        var pipeline = new DaxPipeline("Produto", typeof(object))
        {
            Stages =
            [
                Order(new DaxFunctionCall("COUNTROWS", [new DaxTableRef("Venda")])),
                new DaxProjectStage(
                    [new DaxProjectionColumn("Nome", new DaxColumnRef("Produto[Nome]"))]),
            ],
        };

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => DaxPipelineBuilder.Build(pipeline));

        Assert.Contains("Venda", ex.Message, StringComparison.Ordinal);
    }

    private static DaxOrderStage Order(IDaxExpression expression) =>
        new(new DaxOrderTerm(expression, Ascending: true), ResetsOrder: true);

    private static DaxBinary Times(IDaxExpression left, int right) =>
        new(DaxOperator.Multiply, left, new DaxNumberLiteral(right));
}
