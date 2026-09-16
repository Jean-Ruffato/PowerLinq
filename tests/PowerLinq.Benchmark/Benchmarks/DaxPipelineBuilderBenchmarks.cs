using BenchmarkDotNet.Attributes;
using PowerLinq.DaxConverter.Builders;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.Benchmark.Benchmarks;

/// <summary>
/// Final assembly of the query from an already-resolved <see cref="DaxPipeline"/>: folding the
/// stages in <c>DaxPipelineBuilder</c> plus writing the nodes.
/// </summary>
public class DaxPipelineBuilderBenchmarks
{
    private DaxPipeline _tableOnly = null!;
    private DaxPipeline _withFilter = null!;
    private DaxPipeline _withTopN = null!;
    private DaxPipeline _withOrderBy = null!;
    private DaxPipeline _withTopNAndOrderBy = null!;
    private DaxPipeline _fullyLoaded = null!;

    private static IDaxExpression SimpleFilter() =>
        new DaxBinary(
            DaxOperator.Equal,
            new DaxColumnRef("Produto[Categoria]"),
            DaxLiteral.From("Eletrônicos"));

    /// <summary>
    /// Equivalent to <c>((cat = "X" &amp;&amp; preco &gt; 100) &amp;&amp; (ativo = TRUE || qtd &gt; 0))</c>.
    /// The OR branch forces parentheses to be emitted, exercising the precedence rules.
    /// </summary>
    private static IDaxExpression CompoundFilter()
    {
        var left = new DaxBinary(
            DaxOperator.And,
            SimpleFilter(),
            new DaxBinary(
                DaxOperator.GreaterThan,
                new DaxColumnRef("Produto[Preco]"),
                DaxLiteral.From(100m)));

        var right = new DaxBinary(
            DaxOperator.Or,
            new DaxBinary(
                DaxOperator.Equal, new DaxColumnRef("Produto[Ativo]"), DaxLiteral.From(true)),
            new DaxBinary(
                DaxOperator.GreaterThan, new DaxColumnRef("Produto[Quantidade]"), DaxLiteral.From(0)));

        return new DaxBinary(DaxOperator.And, left, right);
    }

    private static DaxOrderStage Order(string coluna, bool ascendente) =>
        new(new DaxOrderTerm(new DaxColumnRef(coluna), ascendente), ResetsOrder: true);

    /// <summary>An additional term: it does not replace the accumulated ordering.</summary>
    private static DaxOrderStage Then(string coluna, bool ascendente) =>
        new(new DaxOrderTerm(new DaxColumnRef(coluna), ascendente), ResetsOrder: false);

    [GlobalSetup]
    public void Setup()
    {
        _tableOnly = new DaxPipeline("Produto", typeof(object));

        _withFilter = _tableOnly.Then(new DaxFilterStage(SimpleFilter()));

        _withTopN = _tableOnly.Then(new DaxTakeStage(100));

        _withOrderBy = _tableOnly.Then(Order("Produto[Nome]", true));

        _withTopNAndOrderBy = _tableOnly
            .Then(Order("Produto[Preco]", false))
            .Then(new DaxTakeStage(100));

        _fullyLoaded = _tableOnly
            .Then(new DaxFilterStage(CompoundFilter()))
            .Then(Order("Produto[Preco]", false))
            .Then(Then("Produto[Nome]", true))
            .Then(Then("Produto[ProdutoID]", true))
            .Then(new DaxTakeStage(100));
    }

    [Benchmark(Baseline = true, Description = "EVALUATE Tabela")]
    public string TableOnly() => DaxPipelineBuilder.Build(_tableOnly);

    [Benchmark(Description = "FILTER simples")]
    public string WithFilter() => DaxPipelineBuilder.Build(_withFilter);

    [Benchmark(Description = "TOPN sem ordenação")]
    public string WithTopN() => DaxPipelineBuilder.Build(_withTopN);

    [Benchmark(Description = "ORDER BY (cláusula final)")]
    public string WithOrderBy() => DaxPipelineBuilder.Build(_withOrderBy);

    [Benchmark(Description = "TOPN + ordenação (args internos)")]
    public string WithTopNAndOrderBy() => DaxPipelineBuilder.Build(_withTopNAndOrderBy);

    [Benchmark(Description = "FILTER + TOPN + 3 ordenações")]
    public string FullyLoaded() => DaxPipelineBuilder.Build(_fullyLoaded);

    [Benchmark(Description = "COUNTROWS sem filtro")]
    public string CountNoFilter() => DaxPipelineBuilder.BuildCount(_tableOnly);

    [Benchmark(Description = "COUNTROWS com filtro composto")]
    public string CountWithFilter() =>
        DaxPipelineBuilder.BuildCount(_tableOnly.Then(new DaxFilterStage(CompoundFilter())));

    /// <summary>
    /// Tree assembly only, without writing. It isolates the cost a query cache would avoid by
    /// reusing the already-built tree.
    /// </summary>
    [Benchmark(Description = "Montar árvore sem renderizar")]
    public DaxEvaluate BuildSyntaxOnly() => DaxPipelineBuilder.BuildSyntax(_fullyLoaded);
}
