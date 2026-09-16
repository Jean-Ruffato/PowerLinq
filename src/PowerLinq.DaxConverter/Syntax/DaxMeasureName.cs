namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// Resolves a measure's name to its DAX reference. <b>It is the only place that does this.</b>
/// </summary>
/// <remarks>
/// <para>
/// There are two ways to reference a measure — an attribute on the contract when it is a result
/// column, a query method when it <i>is</i> the result — and the risk in that choice is the two
/// diverging on the qualification rule. The mitigation is structural: both paths call this
/// function.
/// </para>
/// <para>
/// It is the same design <c>DaxExpressionVisitor.ResolveResultColumn</c> has had for columns since
/// early on, and for the same reason: two copies of a rule diverge the first time one of them
/// gains a new case.
/// </para>
/// </remarks>
public static class DaxMeasureName
{
    /// <summary>The measure's DAX reference, with the brackets the syntax requires.</summary>
    /// <param name="name">
    /// The name as the caller wrote it, with or without brackets: <c>Total Vendas</c> and
    /// <c>[Total Vendas]</c> resolve the same.
    /// </param>
    /// <exception cref="ArgumentException">The name is empty or only whitespace.</exception>
    /// <remarks>
    /// Accepting both forms is what prevents the likeliest mistake of anyone copying the name from
    /// Power BI: there the measure appears in brackets, and requiring them to be stripped would
    /// produce <c>[[Total Vendas]]</c> — invalid DAX, but only discovered on the server.
    /// </remarks>
    public static DaxMeasureRef Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        string trimmed = name.Trim();

        if (trimmed.Length == 0)
            throw new ArgumentException("A measure name is required.", nameof(name));

        string bare = trimmed.StartsWith('[') && trimmed.EndsWith(']')
            ? trimmed[1..^1]
            : trimmed;

        return new DaxMeasureRef($"[{bare}]");
    }
}
