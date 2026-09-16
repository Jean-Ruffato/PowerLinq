namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// <c>CALCULATETABLE(source, filters...)</c>: applies the filters as <b>filter context</b> over the
/// source.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes filtering by a column of another table expressible. <see cref="DaxFilter"/>
/// opens a <b>row context</b> on the table it iterates, and in that context a reference to another
/// table's column — related or not — is a DAX error (<i>"a single value cannot be determined"</i>).
/// Filter context has no such restriction: it propagates through the model's relationships.
/// </para>
/// <para>
/// The filters are tables, not booleans, even though DAX accepts both forms. That way the same
/// tree serves both paths: here as an argument of <c>CALCULATETABLE</c> and, in the grouped query,
/// as a filter table argument of <c>SUMMARIZECOLUMNS</c> — which is already filter context and
/// therefore needs no wrapping in this node.
/// </para>
/// </remarks>
public sealed record DaxCalculateTable(
    IDaxTableExpression Source,
    IReadOnlyList<IDaxTableExpression> Filters) : IDaxTableExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer)
    {
        writer.Append("CALCULATETABLE(")
              .Indent().Break()
              .Write(Source);

        foreach (IDaxTableExpression filter in Filters)
            writer.Separator().Write(filter);

        writer.Outdent().Break().Append(')');
    }
}
