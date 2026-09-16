namespace PowerLinq.DaxConverter.Syntax;

/// <summary>A reference to a measure of the semantic model — <c>[Total Vendas]</c>.</summary>
/// <remarks>
/// <para>
/// A type separate from <see cref="DaxColumnRef"/> because <b>a measure is not a column</b>, and
/// confusing the two gives a wrong number with no error at all. A column exists per row and needs
/// an iterator — that is why the library emits <c>SUMX</c> and not <c>SUM</c>. A measure is
/// already an aggregation, evaluated in the current filter context; wrapping it in a <c>SUMX</c>
/// would add it up once per row of the iterator.
/// </para>
/// <para>
/// In the syntax the two differ by qualification: <c>Table[Column]</c> carries the table name,
/// <c>[Measure]</c> does not. Since <see cref="DaxColumnRef"/> writes the reference exactly as it
/// came in, a measure passed through it would generate DAX the server accepts — and the
/// distinction would exist only in the mind of whoever wrote it.
/// </para>
/// </remarks>
public sealed record DaxMeasureRef(string Name) : IDaxExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer) => writer.Append(Name);
}
