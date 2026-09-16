namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// <c>CALCULATE(expression, filter context arguments...)</c> — the scalar counterpart of what
/// <see cref="DaxCalculateTable"/> does over a table.
/// </summary>
/// <param name="Expression">The expression to evaluate, usually a measure.</param>
/// <param name="FilterContext">
/// What changes the filter context: filter tables, which <b>apply</b>, and modifiers, which
/// <b>remove</b>.
/// </param>
/// <remarks>
/// <para>
/// <b>The two kinds of argument do opposite things</b>, and that is why the modifiers get their own
/// nodes (<see cref="DaxRemoveFilters"/>, <see cref="DaxAllExcept"/>) instead of being generic
/// function calls. Inside a <c>CALCULATE</c>, <c>Venda[Uf] = "SP"</c> restricts and
/// <c>REMOVEFILTERS(Venda[Uf])</c> unrestricts — the same syntactic position, the inverse effect.
/// </para>
/// <para>
/// With no context argument the node is not emitted at all: <c>CALCULATE([Measure])</c> and
/// <c>[Measure]</c> give the same result, and the builder returns the bare expression. See
/// <c>BuildMeasureSyntax</c>.
/// </para>
/// </remarks>
public sealed record DaxCalculate(
    IDaxExpression Expression,
    IReadOnlyList<IDaxNode> FilterContext) : IDaxExpression
{
    /// <inheritdoc/>
    /// <remarks>
    /// Writes compactly, like <see cref="DaxWriter.WriteCall"/>: <c>CALCULATE</c> shows up inside
    /// the extension column of a <c>SUMMARIZECOLUMNS</c>, and breaking lines there would stack the
    /// expression in the middle of an argument list.
    /// </remarks>
    public void Write(DaxWriter writer) =>
        writer.WriteCall("CALCULATE", [Expression, .. FilterContext]);
}
