namespace PowerLinq.DaxConverter.Syntax;

/// <summary>DAX text, with double quotes escaped by doubling.</summary>
public sealed record DaxTextLiteral(string Value) : IDaxExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer) =>
        writer.Append('"').Append(Value.Replace("\"", "\"\"")).Append('"');
}
