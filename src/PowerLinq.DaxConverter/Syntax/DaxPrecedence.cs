namespace PowerLinq.DaxConverter.Syntax;

/// <summary>Precedences that do not come from a binary operator.</summary>
public static class DaxPrecedence
{
    /// <summary>Nodes that never need parentheses (literals, columns, calls).</summary>
    public const int Atomic = int.MaxValue;
}
