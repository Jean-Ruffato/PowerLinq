using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using PowerLinq.Benchmark.Model;
using PowerLinq.DaxConverter.Builders;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.Benchmark.Benchmarks;

/// <summary>
/// Where the cost of producing DAX lies, split into three <b>independent</b> stages: translating
/// the lambda, folding the pipeline into a tree, and writing the tree as text.
/// </summary>
/// <remarks>
/// <para>
/// It exists to answer the cache's first criterion — "a real gain measured before fixing the
/// architecture" — and it exists <b>separately</b> from <c>DaxQueryCompositionBenchmarks</c> for a
/// concrete reason: there the measurements are "composition" and "composition + writing", and the
/// writing's cost only comes out by subtracting one from the other. With error margins of ±300 ns
/// over differences of 30 ns, that subtraction is not a measurement — which is what the first
/// attempt produced.
/// </para>
/// <para>
/// Here each stage is measured <b>over the previous one's ready-made input</b>, so no number
/// depends on a subtraction:
/// </para>
/// <list type="bullet">
///   <item>translation starts from prebuilt <c>Expression</c> trees;</item>
///   <item>folding starts from a prebuilt <c>DaxPipeline</c>;</item>
///   <item>writing starts from a pre-folded <c>DaxEvaluate</c>.</item>
/// </list>
/// <para>
/// What this is meant to decide: caching the <b>writing</b> is safe and simple, but only pays off
/// if it weighs anything; caching the <b>translation</b> is where the gain would be, and it is what
/// brings the risk of returning the DAX of a different filter.
/// </para>
/// </remarks>
public class DaxCacheOpportunityBenchmarks
{
    [Params(1, 4)]
    public int ChainLength { get; set; }

    private DaxTable<Produto> _table = null!;
    private Expression<Func<Produto, bool>>[] _predicates = null!;
    private DaxPipeline _pipeline = null!;
    private DaxEvaluate _tree = null!;

    [GlobalSetup]
    public void Setup()
    {
        _table = new DaxTable<Produto>(NoopExecutor.Instance);

        _predicates = new Expression<Func<Produto, bool>>[ChainLength];
        for (int i = 0; i < ChainLength; i++)
            _predicates[i] = Predicate(i);

        // The equivalent pipeline, built by hand: the same N filters, already translated.
        var stages = new DaxStage[ChainLength];
        for (int i = 0; i < ChainLength; i++)
            stages[i] = new DaxFilterStage(Translated(i));

        _pipeline = new DaxPipeline("Produto", typeof(Produto)) { Stages = stages };
        _tree = DaxPipelineBuilder.BuildSyntax(_pipeline);
    }

    /// <summary>
    /// Stage 1: <c>Expression</c> → stages. It is the translation cache, and where the earlier
    /// numbers pointed the weight.
    /// </summary>
    [Benchmark(Baseline = true, Description = "1. Traduzir os lambdas")]
    public DaxQuery<Produto> Translate()
    {
        DaxQuery<Produto> query = _table.Where(_predicates[0]);

        for (int i = 1; i < ChainLength; i++)
            query = query.Where(_predicates[i]);

        return query;
    }

    /// <summary>Stage 2: stages → tree. The fold, over an already-built pipeline.</summary>
    [Benchmark(Description = "2. Dobrar o pipeline em árvore")]
    public DaxEvaluate Fold() => DaxPipelineBuilder.BuildSyntax(_pipeline);

    /// <summary>
    /// Stage 3: tree → text. It is the rendering cache — the safe one, because the nodes are
    /// <c>record</c>s and get structural equality for free.
    /// </summary>
    [Benchmark(Description = "3. Escrever a árvore como texto")]
    public string Render() => _tree.ToDaxString();

    /// <summary>The whole path, to check that the three parts add up to the total.</summary>
    [Benchmark(Description = "1+2+3. O caminho inteiro")]
    public string EndToEnd()
    {
        DaxQuery<Produto> query = _table.Where(_predicates[0]);

        for (int i = 1; i < ChainLength; i++)
            query = query.Where(_predicates[i]);

        return query.ToDaxString();
    }

    private static Expression<Func<Produto, bool>> Predicate(int index)
    {
        // A switch with returns, not a switch expression: the arms are lambdas, and the compiler
        // does not infer a common type for them.
        switch (index % 3)
        {
            case 0:
                return p => p.Categoria == "Eletrônicos";
            case 1:
                return p => p.Preco > 100m;
            default:
                return p => p.Ativo;
        }
    }

    private static IDaxExpression Translated(int index) => (index % 3) switch
    {
        0 => (IDaxExpression)new DaxBinary(
            DaxOperator.Equal,
            new DaxColumnRef("Produto[Categoria]"),
            DaxLiteral.From("Eletrônicos")),
        1 => new DaxBinary(
            DaxOperator.GreaterThan,
            new DaxColumnRef("Produto[Preco]"),
            DaxLiteral.From(100m)),
        _ => new DaxColumnRef("Produto[Ativo]")
    };
}
