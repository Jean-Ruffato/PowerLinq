namespace PowerLinq.DaxConverter.Syntax;

/// <summary>One line of the <c>DEFINE</c> block: <c>VAR name = expression</c>.</summary>
/// <remarks>
/// The order of the declarations matters: a <c>VAR</c> only sees the ones declared before it. That
/// is why <see cref="DaxEvaluate.Definitions"/> is a list, not a set.
/// </remarks>
public sealed record DaxVarDefinition(string Name, IDaxNode Expression)
{
    /// <summary>Writes <c>VAR name =</c> followed by the expression.</summary>
    public void Write(DaxWriter writer) =>
        writer.Append("VAR ")
              .Append(Name)
              .Append(" = ")
              .Indent()
              .Write(Expression)
              .Outdent();
}
