namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// A table declared once in the <c>DEFINE</c> block and referenced by name.
/// </summary>
/// <remarks>
/// <para>
/// It exists because pagination repeats the source. <c>Skip</c> generates
/// <c>EXCEPT(source, TOPN(n, source, ...))</c>, with the whole subtree twice, and a following
/// <c>Take</c> brings it to three. Over a large <c>SUMMARIZECOLUMNS</c> that is expensive enough
/// to make server-side pagination unviable — which is exactly what the
/// <c>EXCEPT(TOPN, TOPN)</c> exists to do.
/// </para>
/// <para>
/// The semantics do not change: a query <c>VAR</c> is evaluated once and its result reused. What
/// changes is the size of the text sent and the work the engine avoids repeating.
/// </para>
/// </remarks>
public sealed record DaxVarRef(string Name) : IDaxTableExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer) => writer.Append(Name);
}
