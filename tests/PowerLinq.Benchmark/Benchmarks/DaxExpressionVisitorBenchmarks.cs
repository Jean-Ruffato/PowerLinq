using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using PowerLinq.Benchmark.Model;
using PowerLinq.DaxConverter.Syntax;
using PowerLinq.DaxConverter.Translators;

namespace PowerLinq.Benchmark.Benchmarks;

/// <summary>
/// Translating a LINQ predicate into a DAX string — the current hot path. Each benchmark creates a
/// fresh visitor, the way <c>DaxQuery.Where</c> does. The trees are prebuilt in the setup: what is
/// measured is translation, not the cost Roslyn pays to materialize the expression.
/// </summary>
public class DaxExpressionVisitorBenchmarks
{
    private const string TableName = "Produto";

    // Hand-built literals: they isolate each branch of AppendValue without a Compile()
    private Expression _stringLiteral = null!;
    private Expression _intLiteral = null!;
    private Expression _decimalLiteral = null!;
    private Expression _boolLiteral = null!;
    private Expression _dateTimeLiteral = null!;
    private Expression _nullLiteral = null!;
    private Expression _escapedString = null!;

    // Compiler-generated shapes: they reflect what the user actually writes
    private Expression _simpleEquality = null!;
    private Expression _andAlso = null!;
    private Expression _orElse = null!;
    private Expression _not = null!;
    private Expression _mixedAndOr = null!;
    private Expression _unmappedProperty = null!;
    private Expression _capturedVariable = null!;

    [GlobalSetup]
    public void Setup()
    {
        _stringLiteral = Comparison(nameof(Produto.Categoria), ExpressionType.Equal, "Eletrônicos");
        _intLiteral = Comparison(nameof(Produto.ProdutoId), ExpressionType.Equal, 42);
        _decimalLiteral = Comparison(nameof(Produto.Preco), ExpressionType.GreaterThan, 100.5m);
        _boolLiteral = Comparison(nameof(Produto.Ativo), ExpressionType.Equal, true);
        _dateTimeLiteral = Comparison(
            nameof(Produto.DataCadastro), ExpressionType.GreaterThanOrEqual, new DateTime(2024, 1, 1));
        _nullLiteral = Comparison(nameof(Produto.Nome), ExpressionType.Equal, null, typeof(string));
        _escapedString = Comparison(
            nameof(Produto.Nome), ExpressionType.Equal, "O'Brien \"Premium\" \"Edition\"");

        Expression<Func<Produto, bool>> simple = p => p.Categoria == "Eletrônicos";
        _simpleEquality = simple.Body;

        Expression<Func<Produto, bool>> and = p => p.Categoria == "Eletrônicos" && p.Ativo == true;
        _andAlso = and.Body;

        Expression<Func<Produto, bool>> or = p => p.Categoria == "Eletrônicos" || p.Categoria == "Móveis";
        _orElse = or.Body;

        Expression<Func<Produto, bool>> not = p => !(p.Categoria == "Eletrônicos");
        _not = not.Body;

        Expression<Func<Produto, bool>> mixed = p =>
            (p.Categoria == "Eletrônicos" || p.Categoria == "Móveis")
            && p.Preco > 100m
            && p.Ativo == true;
        _mixedAndOr = mixed.Body;

        // Observacao has no [DaxColumn]: it exercises the Produto[Observacao] fallback
        Expression<Func<Produto, bool>> unmapped = p => p.Observacao == "nota";
        _unmappedProperty = unmapped.Body;

        _capturedVariable = BuildCapturedVariablePredicate();
    }

    [Benchmark(Baseline = true, Description = "Igualdade string (literal)")]
    public string StringLiteral() => Translate(_stringLiteral);

    [Benchmark(Description = "Igualdade int (literal)")]
    public string IntLiteral() => Translate(_intLiteral);

    [Benchmark(Description = "Comparação decimal (literal)")]
    public string DecimalLiteral() => Translate(_decimalLiteral);

    [Benchmark(Description = "Igualdade bool (literal)")]
    public string BoolLiteral() => Translate(_boolLiteral);

    [Benchmark(Description = "Comparação DateTime -> DATE()")]
    public string DateTimeLiteral() => Translate(_dateTimeLiteral);

    [Benchmark(Description = "Comparação com null -> BLANK()")]
    public string NullLiteral() => Translate(_nullLiteral);

    [Benchmark(Description = "String com aspas (escaping)")]
    public string EscapedString() => Translate(_escapedString);

    [Benchmark(Description = "Igualdade simples (lambda)")]
    public string SimpleEquality() => Translate(_simpleEquality);

    [Benchmark(Description = "AndAlso (2 predicados)")]
    public string AndAlso() => Translate(_andAlso);

    [Benchmark(Description = "OrElse (2 predicados)")]
    public string OrElse() => Translate(_orElse);

    [Benchmark(Description = "Not -> NOT()")]
    public string Not() => Translate(_not);

    [Benchmark(Description = "AND/OR misto (4 predicados)")]
    public string MixedAndOr() => Translate(_mixedAndOr);

    [Benchmark(Description = "Propriedade sem [DaxColumn]")]
    public string UnmappedProperty() => Translate(_unmappedProperty);

    /// <summary>
    /// A captured variable: it triggers <c>Expression.Lambda().Compile().DynamicInvoke()</c> inside
    /// <c>VisitMember</c>. Expected to be an order of magnitude above the rest.
    /// </summary>
    [Benchmark(Description = "Variável capturada (Compile + DynamicInvoke)")]
    public string CapturedVariable() => Translate(_capturedVariable);

    // Translate and render: the baseline measured expression -> string in one step, so the
    // comparison is only honest if the writing is part of the measurement.
    private static string Translate(Expression expression) =>
        new DaxExpressionVisitor(TableName).Translate(expression).ToDaxString();

    private static Expression Comparison(
        string property, ExpressionType op, object? value, Type? valueType = null)
    {
        ParameterExpression parameter = Expression.Parameter(typeof(Produto), "p");
        MemberExpression member = Expression.Property(parameter, property);
        ConstantExpression constant = Expression.Constant(value, valueType ?? member.Type);

        return Expression.MakeBinary(op, member, constant);
    }

    private static Expression BuildCapturedVariablePredicate()
    {
        string categoria = "Móveis";
        Expression<Func<Produto, bool>> expr = p => p.Categoria == categoria;

        return expr.Body;
    }
}
