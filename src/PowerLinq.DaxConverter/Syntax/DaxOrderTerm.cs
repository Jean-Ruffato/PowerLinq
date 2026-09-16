namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// An ordering term. It is not an <see cref="IDaxNode"/> because it has no single form: inside
/// <c>TOPN</c> the direction is a comma-separated argument, in the <c>ORDER BY</c> clause it is a
/// suffix on the column.
/// </summary>
public sealed record DaxOrderTerm(IDaxExpression Expression, bool Ascending)
{
    /// <summary>Ordering by a column, which is the most common case.</summary>
    public DaxOrderTerm(DaxColumnRef column, bool ascending)
        : this((IDaxExpression)column, ascending) { }

    /// <summary>
    /// The column, when the term is one — <see langword="null"/> when it is an expression.
    /// </summary>
    /// <remarks>
    /// The distinction survives in one place only: the validation of <c>GroupBy</c>, which compares
    /// the term against the grouping <b>keys</b>. There an expression is not a key, and the refusal
    /// is no limitation — the aggregation collapses the group's rows, so there is no per-output-row
    /// value left for the expression to evaluate. Rewriting against the result of a reshape no
    /// longer needs it: it walks the expression and swaps each reference, instead of matching the
    /// whole term.
    /// </remarks>
    public DaxColumnRef? Column => Expression as DaxColumnRef;

    /// <summary>The form used as a <c>TOPN</c> argument: <c>column, DESC</c>.</summary>
    public void WriteAsArgument(DaxWriter writer) =>
        writer.Write(Expression).Append(Ascending ? ", ASC" : ", DESC");

    /// <summary>The form used in the <c>ORDER BY</c> clause: <c>column DESC</c>.</summary>
    public void WriteAsClause(DaxWriter writer) =>
        writer.Write(Expression).Append(Ascending ? " ASC" : " DESC");
}
