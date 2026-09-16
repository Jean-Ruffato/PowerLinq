namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// Absence of a value, written as <c>BLANK()</c>.
/// </summary>
/// <remarks>
/// <c>BLANK()</c> is a function, not a keyword: there is no <c>null</c> literal in DAX. It is also
/// what an empty column returns, so comparing against it is the equivalent of <c>IS NULL</c>.
/// </remarks>
public sealed record DaxBlankLiteral : IDaxExpression
{
    /// <summary>The single instance — the node has no state.</summary>
    public static readonly DaxBlankLiteral Instance = new();

    private DaxBlankLiteral() { }

    /// <inheritdoc/>
    public void Write(DaxWriter writer) => writer.Append("BLANK()");
}
