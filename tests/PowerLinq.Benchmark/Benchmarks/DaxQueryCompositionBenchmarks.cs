using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using PowerLinq.Benchmark.Model;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Benchmark.Benchmarks;

/// <summary>
/// Chained immutable composition. Two costs add up per link of the chain: the record's <c>with</c>
/// and the filter's reconcatenation in <c>$"({filter} &amp;&amp; {new})"</c> — which recopies all
/// the accumulated text. In <c>OrderBy</c>/<c>ThenBy</c> each link also copies the whole list.
/// </summary>
public class DaxQueryCompositionBenchmarks
{
    [Params(1, 2, 4, 8, 16, 32)]
    public int ChainLength { get; set; }

    private DaxTable<Produto> _table = null!;
    private Expression<Func<Produto, bool>>[] _predicates = null!;
    private Expression<Func<Produto, string>>[] _keySelectors = null!;

    private static readonly string[] StringProperties =
    [
        nameof(Produto.Nome),
        nameof(Produto.Categoria),
        nameof(Produto.Observacao)
    ];

    [GlobalSetup]
    public void Setup()
    {
        _table = new DaxTable<Produto>(NoopExecutor.Instance);

        _predicates = new Expression<Func<Produto, bool>>[ChainLength];
        _keySelectors = new Expression<Func<Produto, string>>[ChainLength];

        for (int i = 0; i < ChainLength; i++)
        {
            _predicates[i] = BuildPredicate(i);
            _keySelectors[i] = BuildKeySelector(StringProperties[i % StringProperties.Length]);
        }
    }

    /// <summary>Chains N <c>Where</c> calls without materializing the DAX.</summary>
    [Benchmark(Baseline = true, Description = "N x Where (composição)")]
    public DaxQuery<Produto> WhereChain()
    {
        DaxQuery<Produto> query = _table.Where(_predicates[0]);

        for (int i = 1; i < ChainLength; i++)
            query = query.Where(_predicates[i]);

        return query;
    }

    /// <summary>Chains N <c>Where</c> calls and materializes — it includes the builder's cost.</summary>
    [Benchmark(Description = "N x Where + ToDaxString")]
    public string WhereChainToDax()
    {
        DaxQuery<Produto> query = _table.Where(_predicates[0]);

        for (int i = 1; i < ChainLength; i++)
            query = query.Where(_predicates[i]);

        return query.ToDaxString();
    }

    [Benchmark(Description = "OrderBy + N x ThenBy")]
    public DaxQuery<Produto> OrderByChain()
    {
        DaxQuery<Produto> query = _table.OrderBy(_keySelectors[0]);

        for (int i = 1; i < ChainLength; i++)
            query = query.ThenBy(_keySelectors[i]);

        return query;
    }

    [Benchmark(Description = "OrderBy + N x ThenBy + ToDaxString")]
    public string OrderByChainToDax()
    {
        DaxQuery<Produto> query = _table.OrderBy(_keySelectors[0]);

        for (int i = 1; i < ChainLength; i++)
            query = query.ThenBy(_keySelectors[i]);

        return query.ToDaxString();
    }

    /// <summary>Take is the cheapest link: just a <c>with</c>, no string.</summary>
    [Benchmark(Description = "N x Take (só record with)")]
    public DaxQuery<Produto> TakeChain()
    {
        DaxQuery<Produto> query = _table.Take(1);

        for (int i = 1; i < ChainLength; i++)
            query = query.Take(i + 1);

        return query;
    }

    private static Expression<Func<Produto, bool>> BuildPredicate(int id)
    {
        ParameterExpression parameter = Expression.Parameter(typeof(Produto), "p");
        BinaryExpression body = Expression.Equal(
            Expression.Property(parameter, nameof(Produto.ProdutoId)),
            Expression.Constant(id));

        return Expression.Lambda<Func<Produto, bool>>(body, parameter);
    }

    private static Expression<Func<Produto, string>> BuildKeySelector(string property)
    {
        ParameterExpression parameter = Expression.Parameter(typeof(Produto), "p");

        return Expression.Lambda<Func<Produto, string>>(
            Expression.Property(parameter, property), parameter);
    }
}
