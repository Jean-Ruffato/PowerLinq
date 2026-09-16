namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// <c>RELATED(Table[Column])</c> — the value of a column from another table, in the current row
/// context.
/// </summary>
/// <param name="Column">The column on the other side of the relationship.</param>
/// <remarks>
/// <para>
/// <b>It is what makes a navigation writable inside a row context.</b> <see cref="DaxFilter"/> and
/// the iterators (<c>SUMX</c>, <c>SELECTCOLUMNS</c>) open a row context on the table they iterate,
/// and there referencing another table's column — related or not — is a DAX error (<i>"a single
/// value cannot be determined"</i>). <c>RELATED</c> resolves the value through the model's
/// relationship.
/// </para>
/// <para>
/// <b>It crosses <i>many-to-one</i>, and only that.</b> The dimension sits on the <i>one</i> side
/// relative to the fact, so starting from the fact works; the other way round does not. That is
/// why navigation leaves the <i>one-to-many</i> side out of scope — that is aggregation
/// (<c>RELATEDTABLE</c>), not scalar navigation. The direction is checked against the model's
/// schema by <c>DaxContractValidator.ValidateNavigation</c>, not here: composing a query touches
/// no network, so at translation time there is no way to know the cardinality.
/// </para>
/// <para>
/// <b>It is not the way to filter.</b> <c>RELATED</c> inside <c>FILTER</c> is row-by-row iteration
/// in the <i>formula engine</i>; the boolean of <c>CALCULATETABLE</c> pushes down as a filter to
/// the <i>storage engine</i>. Same result, costs of a different order on a large fact — and there
/// are filters <c>RELATED</c> cannot even reach, because the table is on the wrong side. To
/// filter, the path is the filter table argument.
/// </para>
/// <para>
/// <b>As a grouping column it would be wrong</b>, not merely unnecessary:
/// <c>SUMMARIZECOLUMNS</c> opens no row context, and there the direct qualified reference is the
/// right form. That is why the translation depends on the position, not only on the expression.
/// </para>
/// </remarks>
public sealed record DaxRelated(DaxColumnRef Column) : IDaxExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer) =>
        writer.Append("RELATED(").Write(Column).Append(')');
}
