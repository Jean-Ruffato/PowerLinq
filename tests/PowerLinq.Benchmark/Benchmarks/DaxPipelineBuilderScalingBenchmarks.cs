using BenchmarkDotNet.Attributes;
using PowerLinq.DaxConverter.Builders;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.Benchmark.Benchmarks;

/// <summary>
/// The builder's scalability along two independent dimensions: the number of ordering terms and
/// the size of the filter. The second axis matters because in the previous version the filter
/// arrived already rendered and was recopied on every interpolation; now it is walked as a tree.
/// </summary>
public class DaxPipelineBuilderScalingBenchmarks
{
    [Params(1, 2, 4, 8, 16, 32)]
    public int Size { get; set; }

    private DaxPipeline _orderByOnly = null!;
    private DaxPipeline _orderByWithTopN = null!;
    private DaxPipeline _largeFilter = null!;

    [GlobalSetup]
    public void Setup()
    {
        var baseDefinition = new DaxPipeline("Produto", typeof(object));

        // The first term replaces the ordering and the following ones add to it — the shape an
        // OrderBy followed by N ThenBy produces.
        var orderStages = new List<DaxStage>(Size);
        for (int i = 0; i < Size; i++)
        {
            orderStages.Add(new DaxOrderStage(
                new DaxOrderTerm(new DaxColumnRef($"Produto[Coluna{i}]"), i % 2 == 0),
                ResetsOrder: i == 0));
        }

        _orderByOnly = baseDefinition with { Stages = orderStages };
        _orderByWithTopN = baseDefinition with { Stages = [.. orderStages, new DaxTakeStage(100)] };
        _largeFilter = baseDefinition.Then(new DaxFilterStage(BuildFilter(Size)));
    }

    [Benchmark(Baseline = true, Description = "N termos em ORDER BY")]
    public string OrderByOnly() => DaxPipelineBuilder.Build(_orderByOnly);

    [Benchmark(Description = "N termos dentro de TOPN")]
    public string OrderByWithTopN() => DaxPipelineBuilder.Build(_orderByWithTopN);

    [Benchmark(Description = "Filtro com N predicados")]
    public string LargeFilter() => DaxPipelineBuilder.Build(_largeFilter);

    [Benchmark(Description = "COUNTROWS com filtro de N predicados")]
    public string CountLargeFilter() => DaxPipelineBuilder.BuildCount(_largeFilter);

    /// <summary>A left-deep conjunction, the shape N calls to Where produce.</summary>
    private static IDaxExpression BuildFilter(int predicateCount)
    {
        IDaxExpression? accumulator = null;

        for (int i = 0; i < predicateCount; i++)
        {
            var predicate = new DaxBinary(
                DaxOperator.Equal,
                new DaxColumnRef($"Produto[Coluna{i}]"),
                DaxLiteral.From(i));

            accumulator = accumulator is null
                ? predicate
                : new DaxBinary(DaxOperator.And, accumulator, predicate);
        }

        return accumulator!;
    }
}
