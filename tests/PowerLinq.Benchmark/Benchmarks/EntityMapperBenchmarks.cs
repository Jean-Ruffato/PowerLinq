using System.Collections.Frozen;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using PowerLinq.Benchmark.Model;
using PowerLinq.DaxConverter.Mapping;

namespace PowerLinq.Benchmark.Benchmarks;

/// <summary>
/// Mapping reflection. None of these results is cached today: <c>GetTableName</c> runs on every
/// <c>new DaxTable&lt;T&gt;</c> and <c>GetColumnMappings</c> on every query execution in the
/// XmlaQueryExecutor.
/// </summary>
public class EntityMapperBenchmarks
{
    private FrozenDictionary<string, DaxColumnMapping> _mappings = null!;
    private FakeDataRecord _row = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mappings = EntityMapper.GetColumnMappings(typeof(Produto));
        _row = FakeDataRecord.CreateProdutoRow(1);
    }

    [Benchmark(Baseline = true, Description = "GetTableName<T> (atributo)")]
    public string GetTableName() => EntityMapper.GetTableName<Produto>();

    [Benchmark(Description = "GetColumnMappings (reflexão completa)")]
    public FrozenDictionary<string, DaxColumnMapping> GetColumnMappings() =>
        EntityMapper.GetColumnMappings(typeof(Produto));

    /// <summary>One row, 9 columns, with a SetValue + Convert.ChangeType per column.</summary>
    [Benchmark(Description = "MapRow (1 linha, 9 colunas)")]
    public Produto MapRow() => EntityMapper.MapRow<Produto>(_row, _mappings);
}
