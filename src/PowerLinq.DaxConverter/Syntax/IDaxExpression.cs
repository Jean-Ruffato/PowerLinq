namespace PowerLinq.DaxConverter.Syntax;

/// <summary>A node that evaluates to a scalar value.</summary>
/// <remarks>
/// The split between scalar and table is the one DAX makes and the string representation erased:
/// <see cref="DaxFilter"/> only accepts a table as its source, and the restriction is now checked
/// by the compiler instead of by the server.
/// </remarks>
public interface IDaxExpression : IDaxNode
{
    /// <summary>
    /// The node's precedence, used by <see cref="DaxWriter.Operand"/> to decide on parentheses.
    /// Indivisible nodes return <see cref="DaxPrecedence.Atomic"/>.
    /// </summary>
    int Precedence => DaxPrecedence.Atomic;
}
