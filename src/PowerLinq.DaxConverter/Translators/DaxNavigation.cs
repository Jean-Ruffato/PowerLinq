using System.Linq.Expressions;
using System.Reflection;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Mapping;

namespace PowerLinq.DaxConverter.Translators;

/// <summary>
/// Recognizes a nested member access that is a navigation, and resolves the destination column.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from <see cref="DaxExpressionVisitor"/> because the result is used in <b>two</b>
/// forms that are not interchangeable: wrapped in <c>RELATED</c> inside a row context, and as a
/// bare qualified reference as a grouping column. The caller decides, because only the caller knows
/// the position.
/// </para>
/// <para>
/// <c>r.Exporter.CnpjRoot</c> arrives as a <c>MemberExpression</c> chained back to the lambda's
/// parameter. The traversal goes outside in — the last member is the column, the middle ones are
/// the navigations — and any link that is not a navigation makes the path not one.
/// </para>
/// </remarks>
internal static class DaxNavigation
{
    /// <summary>
    /// The path, when <paramref name="node"/> is a navigation; <see langword="null"/> when it is
    /// not.
    /// </summary>
    /// <param name="node">The nested member access.</param>
    /// <remarks>
    /// Returning <see langword="null"/> instead of throwing is what keeps the rest of the
    /// translation in place: <c>p.Nome.Length</c> and <c>p.Data.Year</c> are nested members too and
    /// have translations of their own, so this recognizer has to be able to say "not my case".
    /// </remarks>
    internal static DaxNavigationPath? Resolve(MemberExpression node)
    {
        if (node.Member is not PropertyInfo column || node.Expression is not MemberExpression owner)
            return null;

        // The chain of navigations, inside out: the link closest to the parameter first.
        var chain = new List<PropertyInfo>();
        Expression? current = owner;

        while (current is MemberExpression link)
        {
            if (link.Member is not PropertyInfo property
                || property.GetCustomAttribute<DaxNavigationAttribute>() is null)
            {
                return null;
            }

            chain.Insert(0, property);
            current = link.Expression;
        }

        // It only counts starting from the lambda's parameter. A member over a captured variable is
        // a constant and has already been evaluated before reaching here.
        if (current is not ParameterExpression parameter || chain.Count == 0)
            return null;

        var hops = new List<(string From, string To)>(chain.Count);
        string from = EntityMapper.GetTableName(parameter.Type);

        foreach (PropertyInfo navigation in chain)
        {
            string to = EntityMapper.GetTableName(navigation.PropertyType);
            hops.Add((from, to));
            from = to;
        }

        // `from` is now the last hop's table, and it is against that table that the column resolves
        // — not against the source entity's table.
        return new DaxNavigationPath(
            DaxExpressionVisitor.ResolveColumnReference(column, from), hops);
    }

    /// <summary>
    /// Whether the property is a navigation — used by materialization so it does not try to map it.
    /// </summary>
    internal static bool IsNavigation(PropertyInfo property) =>
        property.GetCustomAttribute<DaxNavigationAttribute>() is not null;
}
