namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// <c>FILTER(source, predicate)</c>. The source is necessarily a table — the signature makes it
/// impossible to build a FILTER over a scalar.
/// </summary>
public sealed record DaxFilter(IDaxTableExpression Source, IDaxExpression Predicate)
    : IDaxTableExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer) =>
        writer.Append("FILTER(")
              .Indent().Break()
              .Write(Source)
              .Separator()
              .Write(Predicate)
              .Outdent().Break()
              .Append(')');
}
