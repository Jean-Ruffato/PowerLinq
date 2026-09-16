using System.Linq.Expressions;
using System.Reflection;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Translators;

/// <summary>
/// Translates a <c>Join</c>'s key selectors and result selector into a
/// <see cref="DaxJoinStage"/>.
/// </summary>
/// <remarks>
/// It sits next to <see cref="DaxAggregationTranslator"/> for the same reason: it is expression
/// tree translation, not DAX assembly. The one that builds the <c>GENERATE</c> is
/// <c>DaxPipelineBuilder</c>, from the stage.
/// </remarks>
internal static class DaxJoinTranslator
{
    /// <summary>Translates the join into a stage.</summary>
    /// <param name="outerTableName">The outer side's table, for resolving its columns.</param>
    /// <param name="innerTableName">The inner side's table.</param>
    /// <param name="innerStages">The inner side's stages — filters only, validated by the caller.</param>
    /// <param name="outerResultColumns">
    /// The outer side's result columns, when it has already been through a reshape; it is what
    /// allows chaining one join over another.
    /// </param>
    /// <param name="outerKeySelector">The outer side's key — a property.</param>
    /// <param name="innerKeySelector">The inner side's key — a property.</param>
    /// <param name="resultSelector">The result's object initializer.</param>
    /// <param name="localizer">Language of the error messages.</param>
    public static DaxJoinStage Translate<TOuter, TInner, TKey, TResult>(
        string outerTableName,
        string innerTableName,
        IReadOnlyList<DaxStage> innerStages,
        IReadOnlyList<string>? outerResultColumns,
        Expression<Func<TOuter, TKey>> outerKeySelector,
        Expression<Func<TInner, TKey>> innerKeySelector,
        Expression<Func<TOuter, TInner, TResult>> resultSelector,
        IPowerLinqLocalizer localizer)
    {
        // Reuses GroupBy's key resolution, which already understands `new { a, b }`.
        List<DaxColumnRef> outerKeys = DaxAggregationTranslator.TranslateKey(
            outerKeySelector.Body, outerTableName, localizer, "JoinKeyPropertyRequired", outerResultColumns);

        List<DaxColumnRef> innerKeys = DaxAggregationTranslator.TranslateKey(
            innerKeySelector.Body, innerTableName, localizer, "JoinKeyPropertyRequired");

        // The arity is not checked here: TKey is the same type on both sides, so the compiler
        // itself prevents a composite key with a different number of components. The check lives in
        // the builder, where a hand-built DaxJoinStage can violate it.

        var projections = new List<DaxJoinProjection>();

        ParameterExpression outerParameter = resultSelector.Parameters[0];
        ParameterExpression innerParameter = resultSelector.Parameters[1];

        foreach (MemberBinding binding in Bindings(resultSelector, localizer))
        {
            if (binding is not MemberAssignment { Member: PropertyInfo resultProperty } assignment)
                throw new NotSupportedException(localizer.Get("JoinProjectionDirectOnly"));

            bool fromOuter = SideOf(assignment.Expression, outerParameter, innerParameter, localizer);

            // The expression is translated against its own side, and the result becomes the value
            // of the alias inside that side's SELECTCOLUMNS — before the cross.
            var visitor = new DaxExpressionVisitor(
                fromOuter ? outerTableName : innerTableName,
                localizer,
                fromOuter ? outerResultColumns : null);

            projections.Add(new DaxJoinProjection(
                OutputName(resultProperty),
                fromOuter,
                visitor.Translate(assignment.Expression)));
        }

        return new DaxJoinStage(
            innerTableName,
            innerStages,
            [.. outerKeys.Select(key => key.Reference)],
            [.. innerKeys.Select(key => key.Reference)],
            projections);
    }

    /// <summary>Which side the expression comes from: <see langword="true"/> for the outer one.</summary>
    /// <exception cref="NotSupportedException">
    /// The expression references <b>both</b> parameters, or an unknown one.
    /// </exception>
    /// <remarks>
    /// Each output column is computed inside its own side, before the <c>GENERATE</c> — and there
    /// each side only has its own columns. An expression that spans both could only be computed
    /// after the cross, referencing the aliases, and that is not translated yet. A closed
    /// expression, referencing no parameter at all, goes to the outer side: it is a literal, and
    /// where it is evaluated makes no difference.
    /// </remarks>
    private static bool SideOf(
        Expression expression,
        ParameterExpression outer,
        ParameterExpression inner,
        IPowerLinqLocalizer localizer)
    {
        var collector = new ParameterCollector();
        collector.Visit(expression);

        bool usesOuter = collector.Parameters.Contains(outer);
        bool usesInner = collector.Parameters.Contains(inner);

        if (usesOuter && usesInner)
        {
            throw new NotSupportedException(localizer.Format(
                "JoinProjectionSpansBothSides", expression.ToString()));
        }

        if (collector.Parameters.Any(parameter => parameter != outer && parameter != inner))
            throw new NotSupportedException(localizer.Get("JoinProjectionUnknownParameter"));

        return !usesInner;
    }

    /// <summary>Collects the parameters the expression references.</summary>
    private sealed class ParameterCollector : ExpressionVisitor
    {
        public HashSet<ParameterExpression> Parameters { get; } = [];

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Parameters.Add(node);
            return base.VisitParameter(node);
        }
    }

    private static IEnumerable<MemberBinding> Bindings<TOuter, TInner, TResult>(
        Expression<Func<TOuter, TInner, TResult>> selector,
        IPowerLinqLocalizer localizer) =>
        selector.Body is MemberInitExpression init
            ? init.Bindings
            : throw new NotSupportedException(localizer.Get("JoinProjectionInitializerRequired"));

    /// <summary>
    /// The column's name in the result. Unlike a <c>Select</c> projection, here the absence of
    /// <c>[DaxColumn]</c> falls back to <c>[Property]</c> — the extension-column form — because the
    /// join's result belongs to no table of the model.
    /// </summary>
    private static string OutputName(PropertyInfo property)
    {
        string reference =
            property.GetCustomAttribute<DaxColumnAttribute>()?.ColumnReference ?? $"[{property.Name}]";

        return reference.StartsWith('[') && reference.EndsWith(']') ? reference[1..^1] : reference;
    }
}
