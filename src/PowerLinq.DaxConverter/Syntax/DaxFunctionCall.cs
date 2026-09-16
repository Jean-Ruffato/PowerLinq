namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// A function call that returns a scalar (<c>COUNTROWS</c>, <c>DATE</c>, ...).
/// The arguments are <see cref="IDaxNode"/> because scalar functions can take tables —
/// <c>COUNTROWS</c> is exactly that case.
/// </summary>
public sealed record DaxFunctionCall(string Name, IReadOnlyList<IDaxNode> Arguments)
    : IDaxExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer) => writer.WriteCall(Name, Arguments);
}
