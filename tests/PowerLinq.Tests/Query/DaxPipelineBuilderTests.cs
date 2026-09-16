using PowerLinq.DaxConverter.Builders;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.Tests.Query;

/// <summary>
/// The <b>formatting</b> of the generated DAX, character by character: indentation, line breaks
/// and reindentation of a nested node.
/// </summary>
/// <remarks>
/// It is the only file that compares the exact formatted string. The rest of the suite compares
/// the structure over flattened DAX, so that changing one node's indentation does not fail thirty
/// tests pointing at the wrong place.
/// </remarks>
public class DaxPipelineBuilderTests
{
    private static DaxPipeline Pipeline(params DaxStage[] stages) =>
        new("Produto", typeof(object)) { Stages = stages };

    private static DaxOrderStage Order(string coluna, bool ascendente) =>
        new(new DaxOrderTerm(Column(coluna), ascendente), ResetsOrder: true);

    private static DaxColumnRef Column(string reference) => new(reference);

    private static DaxBinary ColumnEquals(string column, object? value) =>
        new(DaxOperator.Equal, Column(column), DaxLiteral.From(value));

    [Fact]
    public void Build_NoFilters_ReturnsEvaluateTable()
    {
        string dax = DaxPipelineBuilder.Build(Pipeline());

        Assert.Equal("EVALUATE\nProduto", dax);
    }

    [Fact]
    public void Build_WithFilter_WrapsInFilter()
    {
        string dax = DaxPipelineBuilder.Build(
            Pipeline(new DaxFilterStage(ColumnEquals("Produto[Categoria]", "Eletrônicos"))));

        Assert.Contains("FILTER(", dax);
        Assert.Contains("Produto[Categoria] = \"Eletrônicos\"", dax);
    }

    [Fact]
    public void Build_WithTopN_WrapsInTopN()
    {
        string dax = DaxPipelineBuilder.Build(Pipeline(new DaxTakeStage(10)));

        Assert.Contains("TOPN(", dax);
        Assert.Contains("10", dax);
    }

    [Fact]
    public void Build_WithOrderBy_AddsOrderByClause()
    {
        string dax = DaxPipelineBuilder.Build(Pipeline(Order("Produto[Nome]", true)));

        Assert.Contains("ORDER BY Produto[Nome] ASC", dax);
    }

    /// <summary>
    /// The ordering comes out <b>twice</b>, and both are needed for different reasons: as an
    /// argument of the <c>TOPN</c>, which is what picks the rows that enter the window, and as the
    /// <c>EVALUATE</c>'s <c>ORDER BY</c> clause, the only thing DAX guarantees about the order of
    /// the output.
    /// </summary>
    /// <remarks>
    /// Only the first used to be emitted, betting that the <c>TOPN</c> preserved the order. It
    /// usually does, which is worse than not preserving it: it does not show up in a test.
    /// </remarks>
    [Fact]
    public void Build_WithTopNAndOrderBy_EmitsBothTheTopNArgumentAndTheOrderByClause()
    {
        string dax = DaxPipelineBuilder.Build(
            Pipeline(Order("Produto[Preco]", false), new DaxTakeStage(5)));

        Assert.Equal(
            """
            EVALUATE
            TOPN(
                5,
                Produto,
                Produto[Preco], DESC
            )
            ORDER BY Produto[Preco] DESC
            """.ReplaceLineEndings("\n"),
            dax);
    }

    [Fact]
    public void BuildCount_NoFilter_UsesCountRows()
    {
        string dax = DaxPipelineBuilder.BuildCount(new DaxPipeline("Venda", typeof(object)));

        Assert.Equal("EVALUATE\nROW(\"[Count]\", COUNTROWS(Venda))", dax);
    }

    [Fact]
    public void BuildCount_WithFilter_FiltersBeforeCount()
    {
        string dax = DaxPipelineBuilder.BuildCount(new DaxPipeline("Venda", typeof(object))
        {
            Stages = [new DaxFilterStage(ColumnEquals("Venda[Ano]", 2024))]
        });

        Assert.Contains("COUNTROWS(FILTER(Venda, Venda[Ano] = 2024))", dax);
    }

    [Fact]
    public void Build_NestedTopNOverFilter_IndentsConsistently()
    {
        string dax = DaxPipelineBuilder.Build(Pipeline(
            new DaxFilterStage(ColumnEquals("Produto[Categoria]", "Eletrônicos")),
            Order("Produto[Preco]", false),
            new DaxTakeStage(5)));

        // The nested FILTER is reindented relative to the TOPN wrapping it, including the closing
        // parenthesis. The ORDER BY clause comes out at column zero, outside the call.
        Assert.Equal(
            """
            EVALUATE
            TOPN(
                5,
                FILTER(
                    Produto,
                    Produto[Categoria] = "Eletrônicos"
                ),
                Produto[Preco], DESC
            )
            ORDER BY Produto[Preco] DESC
            """.ReplaceLineEndings("\n"),
            dax);
    }

    /// <summary>
    /// The <c>DEFINE</c> block and its indentation. Each <c>VAR</c> comes out on its own line,
    /// indented one level, and the declaration's expression is reindented relative to it — the
    /// <c>EVALUATE</c> goes back to column zero.
    /// </summary>
    [Fact]
    public void Build_SkipOverAFilter_DeclaresTheSourceInADefineBlock()
    {
        string dax = DaxPipelineBuilder.Build(Pipeline(
            new DaxFilterStage(ColumnEquals("Produto[Categoria]", "Eletrônicos")),
            Order("Produto[Preco]", false),
            new DaxSkipStage(20)));

        Assert.Equal(
            """
            DEFINE
                VAR __pl_source_0 = FILTER(
                    Produto,
                    Produto[Categoria] = "Eletrônicos"
                )
            EVALUATE
            EXCEPT(__pl_source_0, TOPN(20, __pl_source_0, Produto[Preco], DESC))
            ORDER BY Produto[Preco] DESC
            """.ReplaceLineEndings("\n"),
            dax);
    }

    [Fact]
    public void Build_MultipleOrderTerms_KeepsDeclaredOrder()
    {
        string dax = DaxPipelineBuilder.Build(Pipeline(
            Order("Produto[Preco]", false),
            new DaxOrderStage(new DaxOrderTerm(Column("Produto[Nome]"), true), ResetsOrder: false)));

        Assert.Contains("ORDER BY Produto[Preco] DESC, Produto[Nome] ASC", dax);
    }

    [Fact]
    public void BuildSyntax_ExposesTreeWithoutRendering()
    {
        DaxEvaluate ast = DaxPipelineBuilder.BuildSyntax(Pipeline(
            new DaxFilterStage(ColumnEquals("Produto[Categoria]", "Eletrônicos")),
            new DaxTakeStage(5)));

        DaxTopN topN = Assert.IsType<DaxTopN>(ast.Source);
        Assert.Equal(5, topN.Count);

        DaxFilter filter = Assert.IsType<DaxFilter>(topN.Source);
        Assert.Equal("Produto", Assert.IsType<DaxTableRef>(filter.Source).Name);
    }
}
