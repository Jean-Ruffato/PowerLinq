namespace PowerLinq.DaxConverter.Syntax;

/// <summary>Logical negation, emitted as the <c>NOT(...)</c> function.</summary>
public sealed record DaxNot(IDaxExpression Operand) : IDaxExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer) =>
        writer.Append("NOT(").Write(Operand).Append(')');
}
