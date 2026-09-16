namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// <c>TOPN(n, source, orderings...)</c>. In DAX a TOPN's orderings are arguments of the function
/// itself, not a separate <c>ORDER BY</c> clause.
/// </summary>
public sealed record DaxTopN(
    IDaxTableExpression Source,
    int Count,
    IReadOnlyList<DaxOrderTerm> OrderBy) : IDaxTableExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer)
    {
        writer.Append("TOPN(")
              .Indent().Break()
              .Append(Count)
              .Separator()
              .Write(Source);

        foreach (DaxOrderTerm term in OrderBy)
            term.WriteAsArgument(writer.Separator());

        writer.Outdent().Break().Append(')');
    }
}
