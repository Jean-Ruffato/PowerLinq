using System.Collections.Frozen;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using PowerLinq.Benchmark.Model;
using PowerLinq.DaxConverter.Mapping;

namespace PowerLinq.Benchmark.Benchmarks;

/// <summary>
/// Result materialization, reproducing <c>XmlaQueryExecutor.ExecuteAsync</c>'s loop: mappings
/// resolved once, <c>MapRow</c> per row. It sets the library's throughput ceiling on large reads —
/// and it is independent of the AST migration.
/// </summary>
public class EntityMapperMaterializationBenchmarks
{
    [Params(1, 10, 100, 1_000, 10_000)]
    public int RowCount { get; set; }

    private FrozenDictionary<string, DaxColumnMapping> _mappings = null!;
    private FakeDataRecord[] _rows = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mappings = EntityMapper.GetColumnMappings(typeof(Produto));
        _rows = new FakeDataRecord[RowCount];

        for (int i = 0; i < RowCount; i++)
            _rows[i] = FakeDataRecord.CreateProdutoRow(i);
    }

    /// <summary>
    /// The same loop over a contract <b>with no parameterless constructor</b>, which materialization
    /// fills through the constructor.
    /// </summary>
    /// <remarks>
    /// The contract has exactly the same columns as the other one, so the measured difference is the
    /// path and not the row's size. And it is more expensive, as expected:
    /// <c>ConstructorInfo.Invoke</c> allocates the argument array per row and is not compiled, while
    /// the property path uses compiled setters and a compiled factory. Measured at ~2.5x in time and
    /// in allocation.
    /// </remarks>
    /// <remarks>
    /// The first measurement said the opposite — the constructor allocating 3.8x <b>less</b> — and
    /// what explained it was not the path: <c>DaxValueConverter.Convert</c> took the property's
    /// description as an argument and interpolated it per cell, a cost only the property path paid.
    /// This benchmark is what exposed that.
    /// </remarks>
    [Benchmark(Description = "Materializar N linhas por construtor")]
    public List<ProdutoImutavel> MapRowsByConstructor()
    {
        FrozenDictionary<string, DaxColumnMapping> mappings =
            EntityMapper.GetColumnMappings(typeof(ProdutoImutavel));

        var results = new List<ProdutoImutavel>(RowCount);

        for (int i = 0; i < RowCount; i++)
            results.Add(EntityMapper.MapRow<ProdutoImutavel>(_rows[i], mappings));

        return results;
    }

    /// <summary>The mapping loop only, with the mappings already resolved.</summary>
    [Benchmark(Baseline = true, Description = "Materializar N linhas")]
    public List<Produto> MapRows()
    {
        var results = new List<Produto>(RowCount);

        for (int i = 0; i < RowCount; i++)
            results.Add(EntityMapper.MapRow<Produto>(_rows[i], _mappings));

        return results;
    }

    /// <summary>
    /// It includes resolving the mappings, as actually happens on every query. The difference from
    /// <see cref="MapRows"/> is the fixed cost per execution.
    /// </summary>
    [Benchmark(Description = "Materializar N linhas + resolver mappings")]
    public List<Produto> MapRowsWithMappingResolution()
    {
        FrozenDictionary<string, DaxColumnMapping> mappings = EntityMapper.GetColumnMappings(typeof(Produto));
        var results = new List<Produto>(RowCount);

        for (int i = 0; i < RowCount; i++)
            results.Add(EntityMapper.MapRow<Produto>(_rows[i], mappings));

        return results;
    }
}
