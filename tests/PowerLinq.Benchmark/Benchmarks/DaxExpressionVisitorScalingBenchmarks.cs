using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using PowerLinq.Benchmark.Model;
using PowerLinq.DaxConverter.Syntax;
using PowerLinq.DaxConverter.Translators;

namespace PowerLinq.Benchmark.Benchmarks;

/// <summary>
/// How translation scales with the tree's size. It is the most relevant chart for the AST
/// migration: it shows whether the cost is linear in the number of nodes or whether the
/// <c>StringBuilder</c> introduces non-linearity through reallocation.
/// </summary>
public class DaxExpressionVisitorScalingBenchmarks
{
    private const string TableName = "Produto";

    [Params(1, 2, 4, 8, 16, 32, 64)]
    public int PredicateCount { get; set; }

    private Expression _conjunction = null!;
    private Expression _disjunction = null!;
    private Expression _balanced = null!;

    [GlobalSetup]
    public void Setup()
    {
        _conjunction = BuildChain(ExpressionType.AndAlso);
        _disjunction = BuildChain(ExpressionType.OrElse);
        _balanced = BuildBalancedTree(PredicateCount);
    }

    /// <summary>A left-degenerate tree: ((((a &amp;&amp; b) &amp;&amp; c) &amp;&amp; d)...)</summary>
    [Benchmark(Baseline = true, Description = "Cadeia AND (esquerda-profunda)")]
    public string Conjunction() => Translate(_conjunction);

    [Benchmark(Description = "Cadeia OR (esquerda-profunda)")]
    public string Disjunction() => Translate(_disjunction);

    /// <summary>A balanced tree: the same number of nodes, depth log(n).</summary>
    [Benchmark(Description = "Árvore AND balanceada")]
    public string Balanced() => Translate(_balanced);

    // Translate and render: the baseline measured expression -> string in one step, so the
    // comparison is only honest if the writing is part of the measurement.
    private static string Translate(Expression expression) =>
        new DaxExpressionVisitor(TableName).Translate(expression).ToDaxString();

    private Expression BuildChain(ExpressionType op)
    {
        ParameterExpression parameter = Expression.Parameter(typeof(Produto), "p");
        Expression? accumulator = null;

        for (int i = 0; i < PredicateCount; i++)
        {
            Expression predicate = Leaf(parameter, i);
            accumulator = accumulator is null
                ? predicate
                : Expression.MakeBinary(op, accumulator, predicate);
        }

        return accumulator!;
    }

    private static Expression BuildBalancedTree(int count)
    {
        ParameterExpression parameter = Expression.Parameter(typeof(Produto), "p");
        var level = new List<Expression>(count);

        for (int i = 0; i < count; i++)
            level.Add(Leaf(parameter, i));

        while (level.Count > 1)
        {
            var next = new List<Expression>((level.Count + 1) / 2);

            for (int i = 0; i < level.Count; i += 2)
            {
                next.Add(i + 1 < level.Count
                    ? Expression.AndAlso(level[i], level[i + 1])
                    : level[i]);
            }

            level = next;
        }

        return level[0];
    }

    /// <summary>A leaf alternating across columns and types, so as not to measure a single case.</summary>
    private static Expression Leaf(ParameterExpression parameter, int index) => (index % 3) switch
    {
        0 => Expression.Equal(
            Expression.Property(parameter, nameof(Produto.ProdutoId)),
            Expression.Constant(index)),
        1 => Expression.Equal(
            Expression.Property(parameter, nameof(Produto.Categoria)),
            Expression.Constant($"Categoria {index}")),
        _ => Expression.GreaterThan(
            Expression.Property(parameter, nameof(Produto.Preco)),
            Expression.Constant((decimal)index))
    };
}
