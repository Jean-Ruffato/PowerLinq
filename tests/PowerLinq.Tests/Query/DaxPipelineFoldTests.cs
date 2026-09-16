using PowerLinq.DaxConverter.Builders;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.Tests.Query;

/// <summary>
/// The DAX each sequence of stages produces, and what the sequence manages to distinguish.
/// </summary>
/// <remarks>
/// <para>
/// This file was born as an <b>equivalence</b> test: during the migration it compared the
/// <c>DaxPipelineBuilder</c>'s DAX with <c>DaxQueryBuilder</c>'s over the flat
/// <c>DaxQueryDefinition</c>, to prove the new representation reproduced the old one character by
/// character before any consumer depended on it.
/// </para>
/// <para>
/// With the migration finished, the two old classes were deleted — and the expectations were
/// <b>frozen as literals</b>. What the file proves changed from "the two implementations agree" to
/// "these twelve compositions produce this DAX", which is the property that always mattered:
/// keeping a second implementation just to compare against would mean maintaining two.
/// </para>
/// <para>
/// The <c>TOPN</c> comes accompanied by the <c>ORDER BY</c> clause: the <c>TOPN</c> chooses which
/// rows come in, and only the <c>EVALUATE</c>'s <c>ORDER BY</c> guarantees the order they come out in.
/// </para>
/// <para>
/// The comparison is over the <b>flattened</b> DAX. The exact formatting — indentation, line
/// breaks, reindentation of a nested node — is pinned down character by character by
/// <c>DaxPipelineBuilderTests</c>.
/// </para>
/// </remarks>
public sealed class DaxPipelineFoldTests
{
    private static readonly DaxColumnRef Preco = new("Produto[Preco]");
    private static readonly DaxColumnRef Nome = new("Produto[Nome]");

    private static IDaxExpression Ativo() =>
        new DaxBinary(DaxOperator.Equal, new DaxColumnRef("Produto[Ativo]"), DaxLiteral.From(true));

    private static IDaxExpression PrecoAcima(decimal valor) =>
        new DaxBinary(DaxOperator.GreaterThan, Preco, DaxLiteral.From(valor));

    private static IDaxExpression Igual(string coluna, string valor) =>
        new DaxBinary(DaxOperator.Equal, new DaxColumnRef(coluna), DaxLiteral.From(valor));

    private static DaxPipeline Pipeline(params DaxStage[] stages) =>
        new("Produto", typeof(object)) { Stages = stages };

    private static DaxOrderStage Order(DaxColumnRef coluna, bool ascendente, bool substitui = true) =>
        new(new DaxOrderTerm(coluna, ascendente), ResetsOrder: substitui);

    /// <summary>
    /// Collapses the formatting: line breaks and indentation become a single space, and the space
    /// next to a parenthesis goes away.
    /// </summary>
    private static string Flat(string dax)
    {
        string collapsed = string.Join(
            ' ', dax.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Replace("( ", "(", StringComparison.Ordinal)
                        .Replace(" )", ")", StringComparison.Ordinal);
    }

    /// <summary>
    /// The compositions the flat representation could express, with the DAX of each. Each case is
    /// named, so a failure says which composition changed instead of just an index.
    /// </summary>
    public static TheoryData<string, DaxPipeline, string> Composicoes() => new()
    {
        {
            "tabela nua",
            Pipeline(),
            "EVALUATE Produto"
        },
        {
            "where",
            Pipeline(new DaxFilterStage(Ativo())),
            "EVALUATE FILTER(Produto, Produto[Ativo] = TRUE)"
        },
        {
            "order",
            Pipeline(Order(Preco, ascendente: false)),
            "EVALUATE Produto ORDER BY Produto[Preco] DESC"
        },
        {
            "order com dois termos",
            Pipeline(Order(Preco, false), Order(Nome, true, substitui: false)),
            "EVALUATE Produto ORDER BY Produto[Preco] DESC, Produto[Nome] ASC"
        },
        {
            "take sem order",
            Pipeline(new DaxTakeStage(5)),
            "EVALUATE TOPN(5, Produto)"
        },
        {
            "order e take",
            Pipeline(Order(Preco, false), new DaxTakeStage(5)),
            "EVALUATE TOPN(5, Produto, Produto[Preco], DESC) ORDER BY Produto[Preco] DESC"
        },
        {
            "order e skip",
            Pipeline(Order(Preco, true), new DaxSkipStage(10)),
            "EVALUATE EXCEPT(Produto, TOPN(10, Produto, Produto[Preco], ASC)) "
                + "ORDER BY Produto[Preco] ASC"
        },
        {
            "order, skip e take — a paginação",
            Pipeline(Order(Preco, true), new DaxSkipStage(10), new DaxTakeStage(5)),
            "EVALUATE TOPN(5, EXCEPT(Produto, TOPN(10, Produto, Produto[Preco], ASC)), "
                + "Produto[Preco], ASC) ORDER BY Produto[Preco] ASC"
        },
        {
            "where, order, skip e take — a ordem canônica inteira",
            Pipeline(
                new DaxFilterStage(Ativo()),
                Order(Preco, false),
                Order(Nome, true, substitui: false),
                new DaxSkipStage(20),
                new DaxTakeStage(10)),
            // The FILTER is declared once: the Skip's EXCEPT references it twice, and before the
            // VAR it was written — and evaluated — twice.
            "DEFINE VAR __pl_source_0 = FILTER(Produto, Produto[Ativo] = TRUE) "
                + "EVALUATE TOPN(10, EXCEPT(__pl_source_0, "
                + "TOPN(20, __pl_source_0, Produto[Preco], DESC, "
                + "Produto[Nome], ASC)), Produto[Preco], DESC, Produto[Nome], ASC) "
                + "ORDER BY Produto[Preco] DESC, Produto[Nome] ASC"
        },
        {
            "where de outra tabela",
            Pipeline(new DaxRelatedFilterStage("Categoria", Igual("Categoria[Nome]", "Eletrônicos"))),
            "EVALUATE CALCULATETABLE(Produto, FILTER(Categoria, Categoria[Nome] = \"Eletrônicos\"))"
        },
        {
            "where próprio e de duas outras tabelas",
            Pipeline(
                new DaxFilterStage(PrecoAcima(100m)),
                new DaxRelatedFilterStage("Categoria", Igual("Categoria[Nome]", "A")),
                new DaxRelatedFilterStage(
                    "Fornecedor",
                    new DaxBinary(
                        DaxOperator.Equal,
                        new DaxColumnRef("Fornecedor[Ativo]"),
                        DaxLiteral.From(true)))),
            "EVALUATE CALCULATETABLE(FILTER(Produto, Produto[Preco] > 100), "
                + "FILTER(Categoria, Categoria[Nome] = \"A\"), "
                + "FILTER(Fornecedor, Fornecedor[Ativo] = TRUE))"
        },
        {
            "where de outra tabela com janela",
            Pipeline(
                new DaxFilterStage(Ativo()),
                new DaxRelatedFilterStage("Categoria", Igual("Categoria[Nome]", "A")),
                Order(Preco, false),
                new DaxTakeStage(3)),
            "EVALUATE TOPN(3, CALCULATETABLE(FILTER(Produto, Produto[Ativo] = TRUE), "
                + "FILTER(Categoria, Categoria[Nome] = \"A\")), Produto[Preco], DESC) "
                + "ORDER BY Produto[Preco] DESC"
        }
    };

    [Theory]
    [MemberData(nameof(Composicoes))]
    public void EachComposition_ProducesItsOwnDax(string caso, DaxPipeline pipeline, string esperado)
    {
        Assert.Equal(esperado, Flat(DaxPipelineBuilder.Build(pipeline)));
        Assert.False(string.IsNullOrWhiteSpace(caso));
    }

    /// <summary>
    /// The count wraps the folded source in <c>COUNTROWS</c> and discards the pending ordering,
    /// because ordering a number means nothing — but it keeps the <b>window</b>.
    /// </summary>
    [Fact]
    public void Count_WrapsTheFoldedSource_AndKeepsTheWindow()
    {
        DaxPipeline pipeline = Pipeline(
            new DaxFilterStage(Ativo()), Order(Preco, false), new DaxTakeStage(3));

        Assert.Equal(
            "EVALUATE ROW(\"[Count]\", COUNTROWS(TOPN(3, FILTER(Produto, Produto[Ativo] = TRUE), "
                + "Produto[Preco], DESC)))",
            Flat(DaxPipelineBuilder.BuildCount(pipeline)));
    }

    [Fact]
    public void Scalar_WrapsTheFoldedSource_InTheIterator()
    {
        DaxPipeline pipeline = Pipeline(new DaxFilterStage(Ativo()));

        Assert.Equal(
            "EVALUATE ROW(\"[Value]\", SUMX(FILTER(Produto, Produto[Ativo] = TRUE), Produto[Preco]))",
            Flat(DaxPipelineBuilder.BuildScalar(pipeline, "SUMX", Preco)));
    }

    /// <summary>
    /// The refusal of a <c>Skip</c> with no ordering comes from DAX — pagination without order is
    /// not deterministic — not from the structure.
    /// </summary>
    [Fact]
    public void Skip_WithoutOrder_Throws()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => DaxPipelineBuilder.Build(Pipeline(new DaxSkipStage(10))));

        Assert.Equal("Skip requires an OrderBy to produce deterministic results.", ex.Message);
    }

    [Fact]
    public void Skip_WithoutOrder_ThrowsInPortuguese()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => DaxPipelineBuilder.Build(
                Pipeline(new DaxSkipStage(10)), new ResourceManagerPowerLinqLocalizer("pt-BR")));

        Assert.StartsWith("Skip requer um OrderBy", ex.Message);
    }

    // ---------- what the stages' order distinguishes ----------

    /// <summary>
    /// The property that justifies the reshape. Both compositions produced the <b>same</b>
    /// <c>DaxQueryDefinition</c> value — four independent fields, with no order among them — and
    /// that is why one of them had to be refused: there was nothing to generate.
    /// </summary>
    [Fact]
    public void FilterThenTake_AndTakeThenFilter_ProduceDifferentDax()
    {
        string filtraDepoisLimita = Flat(DaxPipelineBuilder.Build(
            Pipeline(new DaxFilterStage(Ativo()), new DaxTakeStage(5))));

        string limitaDepoisFiltra = Flat(DaxPipelineBuilder.Build(
            Pipeline(new DaxTakeStage(5), new DaxFilterStage(Ativo()))));

        Assert.Equal("EVALUATE TOPN(5, FILTER(Produto, Produto[Ativo] = TRUE))", filtraDepoisLimita);
        Assert.Equal("EVALUATE FILTER(TOPN(5, Produto), Produto[Ativo] = TRUE)", limitaDepoisFiltra);
    }

    /// <summary>
    /// <c>Take</c> consumes the pending ordering; an ordering declared afterwards applies to the
    /// window's result, and becomes the <c>EVALUATE</c>'s <c>ORDER BY</c> clause.
    /// </summary>
    [Fact]
    public void OrderAfterTake_AppliesToTheWindow_NotToTheTable()
    {
        string dax = Flat(DaxPipelineBuilder.Build(
            Pipeline(Order(Preco, false), new DaxTakeStage(5), Order(Nome, true))));

        Assert.Equal("EVALUATE TOPN(5, Produto, Produto[Preco], DESC) ORDER BY Produto[Nome] ASC", dax);
    }

    /// <summary>
    /// <c>Skip</c> does <b>not</b> consume the ordering: <c>Skip(10).Take(5)</c> needs the same
    /// terms in both windows, otherwise the <c>EXCEPT</c> and the <c>TOPN</c> would disagree about
    /// which rows are the first.
    /// </summary>
    [Fact]
    public void SkipDoesNotConsumeTheOrder_SoTheWindowsAgree()
    {
        string dax = Flat(DaxPipelineBuilder.Build(
            Pipeline(Order(Preco, true), new DaxSkipStage(10), new DaxTakeStage(5))));

        Assert.Equal(2, dax.Split("Produto[Preco], ASC").Length - 1);
        Assert.Contains("EXCEPT(", dax, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>OrderBy</c> replaces the ordering and <c>ThenBy</c> adds to it — LINQ's semantics, which
    /// the flat representation did not distinguish.
    /// </summary>
    [Fact]
    public void ResettingOrderStage_ReplacesWhileTheOtherAppends()
    {
        string substitui = Flat(DaxPipelineBuilder.Build(
            Pipeline(Order(Preco, false), Order(Nome, true))));

        string acrescenta = Flat(DaxPipelineBuilder.Build(
            Pipeline(Order(Preco, false), Order(Nome, true, substitui: false))));

        Assert.Equal("EVALUATE Produto ORDER BY Produto[Nome] ASC", substitui);
        Assert.Equal("EVALUATE Produto ORDER BY Produto[Preco] DESC, Produto[Nome] ASC", acrescenta);
    }

    /// <summary>
    /// Consecutive filters over other tables become <b>one</b> <c>CALCULATETABLE</c>, and predicates
    /// over the same table are merged with <c>&amp;&amp;</c> into a single <c>FILTER</c>.
    /// </summary>
    [Fact]
    public void ConsecutiveRelatedFilters_CollapseIntoOneCalculateTable()
    {
        string dax = Flat(DaxPipelineBuilder.Build(Pipeline(
            new DaxRelatedFilterStage("Categoria", Igual("Categoria[Nome]", "A")),
            new DaxRelatedFilterStage("Categoria", Igual("Categoria[Grupo]", "B")),
            new DaxRelatedFilterStage("Fornecedor", Igual("Fornecedor[Uf]", "SP")))));

        Assert.Equal(1, dax.Split("CALCULATETABLE(").Length - 1);
        // The two Categoria predicates merged into a single FILTER, and Fornecedor in its own.
        Assert.Contains(
            "FILTER(Categoria, Categoria[Nome] = \"A\" && Categoria[Grupo] = \"B\")",
            dax,
            StringComparison.Ordinal);
        Assert.Contains("FILTER(Fornecedor, Fornecedor[Uf] = \"SP\")", dax, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsecutiveOwnFilters_CollapseIntoOneFilterWithAConjunction()
    {
        string dax = Flat(DaxPipelineBuilder.Build(Pipeline(
            new DaxFilterStage(Ativo()), new DaxFilterStage(PrecoAcima(100m)))));

        Assert.Equal(
            "EVALUATE FILTER(Produto, Produto[Ativo] = TRUE && Produto[Preco] > 100)",
            dax);
    }
}
