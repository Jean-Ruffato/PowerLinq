namespace PowerLinq.DaxConverter.Syntax;

/// <summary>A function call that returns a table (<c>ROW</c>, <c>SUMMARIZECOLUMNS</c>, ...).</summary>
public sealed record DaxTableFunctionCall(string Name, IReadOnlyList<IDaxNode> Arguments)
    : IDaxTableExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer) => writer.WriteCall(Name, Arguments);
}
