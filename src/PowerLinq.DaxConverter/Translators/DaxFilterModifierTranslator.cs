using System.Linq.Expressions;
using System.Reflection;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Translators;

/// <summary>
/// Translates the targets of a filter-context modifier — the columns whose filter is removed or
/// kept.
/// </summary>
/// <remarks>
/// <para>
/// <b>It sits apart from the expression translator because what it accepts is a different thing.</b>
/// A modifier does not take a predicate, it takes a <b>column reference</b>: the target of
/// <c>REMOVEFILTERS</c> is <c>Venda[Uf]</c>, not <c>Venda[Uf] = "SP"</c>. Both forms occupy the same
/// position in a <c>CALCULATE</c> and do <b>opposite</b> things — one removes that column's filter,
/// the other applies one — and that is why passing a predicate here is refused rather than
/// translated.
/// </para>
/// <para>
/// It was the anticipated risk: <i>"an API that treats them as an ordinary predicate will generate
/// DAX that says the opposite of what the user wrote"</i>. The API's shape already pushes toward
/// the right side — the parameter is a selector, not a predicate — and this refusal closes what the
/// shape does not prevent, because <c>v =&gt; v.Uf == "SP"</c> is a valid selector for the compiler
/// too.
/// </para>
/// </remarks>
internal static class DaxFilterModifierTranslator
{
    /// <summary>
    /// The DAX reference of the column the selector points at.
    /// </summary>
    /// <param name="selector">The selector's body — <c>v =&gt; v.Uf</c>.</param>
    /// <param name="tableName">The table the column is resolved against.</param>
    /// <param name="localizer">Language of the error messages.</param>
    /// <exception cref="NotSupportedException">
    /// The body is not a property access on the parameter — typically because the caller passed a
    /// predicate.
    /// </exception>
    internal static string Column(
        Expression selector,
        string tableName,
        IPowerLinqLocalizer localizer)
    {
        // The conversion to object that `Func<T, object?>` inserts is crossed over: it belongs to
        // the compiler, not to what the caller wrote.
        Expression body = Unwrap(selector);

        if (body is MemberExpression { Expression: ParameterExpression, Member: PropertyInfo property })
            return DaxExpressionVisitor.ResolveColumnReference(property, tableName);

        throw new NotSupportedException(localizer.Format(
            "FilterModifierNeedsAColumn", Describe(body)));
    }

    /// <summary>
    /// The references of an array of selectors, in the order they were passed.
    /// </summary>
    /// <param name="selectors">
    /// The array node the <c>params</c> produced in the expression tree, or a list of lambdas.
    /// </param>
    /// <param name="tableName">The table the columns are resolved against.</param>
    /// <param name="localizer">Language of the error messages.</param>
    /// <remarks>
    /// A <c>params</c> inside an expression tree arrives as a <see cref="NewArrayExpression"/> of
    /// quoted lambdas, not as a ready-made array — that is the shape the compiler gives it, and
    /// unwrapping it here is what allows <c>g.MeasureIgnoring("m", v =&gt; v.Uf)</c> to be written
    /// inside a <c>Select</c>.
    /// </remarks>
    internal static List<string> Columns(
        Expression selectors,
        string tableName,
        IPowerLinqLocalizer localizer)
    {
        if (Unwrap(selectors) is not NewArrayExpression array)
        {
            throw new NotSupportedException(localizer.Format(
                "FilterModifierNeedsAColumn", Describe(selectors)));
        }

        var columns = new List<string>(array.Expressions.Count);

        foreach (Expression element in array.Expressions)
        {
            Expression body = Unwrap(element) is LambdaExpression lambda
                ? lambda.Body
                : element;

            columns.Add(Column(body, tableName, localizer));
        }

        return columns;
    }

    /// <summary>
    /// Strips the conversions and quotes the compiler inserts, reaching what was actually written.
    /// </summary>
    private static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression
               {
                   NodeType: ExpressionType.Convert
                       or ExpressionType.ConvertChecked
                       or ExpressionType.Quote
               } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    /// <summary>
    /// How what came in is described in the message.
    /// </summary>
    /// <remarks>
    /// A predicate gets a description of its own because it is <b>the</b> error this refusal exists
    /// to catch, and saying "expected a column, got Equal" would send the author looking for a type
    /// problem instead of realizing that removing and applying a filter are different things.
    /// </remarks>
    private static string Describe(Expression body) => body switch
    {
        BinaryExpression or MethodCallExpression when body.Type == typeof(bool) => "a predicate",
        LambdaExpression lambda when lambda.ReturnType == typeof(bool) => "a predicate",
        _ => body.ToString(),
    };
}
