using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using PowerLinq.Benchmark.Model;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Benchmark.Benchmarks;

/// <summary>
/// The complete pipeline, from <see cref="DaxTable{T}"/> to the DAX string. These are the numbers
/// to compare before and after the AST migration. The Inline/Prebuilt pairs separate PowerLinq's
/// cost from the cost Roslyn pays to materialize the expression tree on every call.
/// </summary>
public class EndToEndQueryBenchmarks
{
    private DaxTable<Produto> _table = null!;

    private Expression<Func<Produto, bool>> _simplePredicate = null!;
    private Expression<Func<Produto, bool>> _compoundPredicate = null!;
    private Expression<Func<Produto, decimal>> _precoSelector = null!;
    private Expression<Func<Produto, string>> _nomeSelector = null!;

    [GlobalSetup]
    public void Setup()
    {
        _table = new DaxTable<Produto>(NoopExecutor.Instance);

        _simplePredicate = p => p.Categoria == "Eletrônicos";
        _compoundPredicate = p => p.Categoria == "Eletrônicos" && p.Preco > 100m && p.Ativo == true;
        _precoSelector = p => p.Preco;
        _nomeSelector = p => p.Nome;
    }

    /// <summary>Building the table: it triggers <c>EntityMapper.GetTableName</c>.</summary>
    [Benchmark(Description = "new DaxTable<T>")]
    public DaxTable<Produto> TableConstruction() => new(NoopExecutor.Instance);

    [Benchmark(Baseline = true, Description = "Where + ToDaxString (lambda inline)")]
    public string SimpleInline() =>
        _table.Where(p => p.Categoria == "Eletrônicos").ToDaxString();

    [Benchmark(Description = "Where + ToDaxString (lambda pré-construída)")]
    public string SimplePrebuilt() =>
        _table.Where(_simplePredicate).ToDaxString();

    [Benchmark(Description = "Where composto + ToDaxString (inline)")]
    public string CompoundInline() =>
        _table.Where(p => p.Categoria == "Eletrônicos" && p.Preco > 100m && p.Ativo == true)
              .ToDaxString();

    [Benchmark(Description = "Where composto + ToDaxString (pré-construída)")]
    public string CompoundPrebuilt() =>
        _table.Where(_compoundPredicate).ToDaxString();

    /// <summary>The most common shape in real use: filter, sort, limit.</summary>
    [Benchmark(Description = "Where + OrderByDescending + Take + ToDaxString")]
    public string TypicalQuery() =>
        _table.Where(_simplePredicate)
              .OrderByDescending(_precoSelector)
              .Take(100)
              .ToDaxString();

    [Benchmark(Description = "Query complexa (2 Where, 2 ordenações, Take)")]
    public string ComplexQuery() =>
        _table.Where(_simplePredicate)
              .Where(_compoundPredicate)
              .OrderByDescending(_precoSelector)
              .ThenBy(_nomeSelector)
              .Take(100)
              .ToDaxString();

    [Benchmark(Description = "ToListAsync (executor noop)")]
    public Task<List<Produto>> ToListAsync() =>
        _table.Where(_simplePredicate).ToListAsync();

    [Benchmark(Description = "FirstOrDefaultAsync (executor noop)")]
    public Task<Produto?> FirstOrDefaultAsync() =>
        _table.Where(_simplePredicate).FirstOrDefaultAsync();

    [Benchmark(Description = "FirstAsync (executor noop)")]
    public Task<Produto> FirstAsync() =>
        _table.Where(_simplePredicate).FirstAsync();

    [Benchmark(Description = "CountAsync (executor noop)")]
    public Task<int> CountAsync() =>
        _table.Where(_simplePredicate).CountAsync();

    /// <summary>Direct entry points on DaxTable, without going through DaxQuery first.</summary>
    [Benchmark(Description = "DaxTable.CountAsync (sem filtro)")]
    public Task<int> TableCountAsync() => _table.CountAsync();

    [Benchmark(Description = "DaxTable.ToListAsync (sem filtro)")]
    public Task<List<Produto>> TableToListAsync() => _table.ToListAsync();
}
