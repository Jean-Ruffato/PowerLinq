namespace PowerLinq.DaxConverter.Syntax;

/// <summary>Binary operation, with parentheses decided by precedence.</summary>
public sealed record DaxBinary(DaxOperator Operator, IDaxExpression Left, IDaxExpression Right)
    : IDaxExpression
{
    /// <inheritdoc/>
    public int Precedence => Operator.Precedence;

    /// <inheritdoc/>
    public void Write(DaxWriter writer) =>
        writer.Operand(Left, Operator.Precedence)
              .Append(' ')
              .Append(Operator.Symbol)
              .Append(' ')
              // Non-associative operators require parentheses on a right operand
              // of the same precedence: a - (b - c) is not a - b - c.
              .Operand(Right, Operator.IsAssociative ? Operator.Precedence : Operator.Precedence + 1);
}
