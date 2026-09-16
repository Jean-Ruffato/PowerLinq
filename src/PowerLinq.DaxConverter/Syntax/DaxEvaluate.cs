namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// The <c>EVALUATE</c> statement — the root of a complete DAX query.
/// <paramref name="OrderBy"/> is the trailing clause; orderings that belong to a
/// <see cref="DaxTopN"/> live inside that node instead.
/// </summary>
public sealed record DaxEvaluate(
    IDaxTableExpression Source,
    IReadOnlyList<DaxOrderTerm> OrderBy,
    IReadOnlyList<DaxVarDefinition> Definitions) : IDaxNode
{
    /// <summary><c>EVALUATE</c> with no ordering clause.</summary>
    public DaxEvaluate(IDaxTableExpression source) : this(source, [], []) { }

    /// <summary><c>EVALUATE</c> with no <c>DEFINE</c> block.</summary>
    public DaxEvaluate(IDaxTableExpression source, IReadOnlyList<DaxOrderTerm> orderBy)
        : this(source, orderBy, []) { }

    /// <inheritdoc/>
    public void Write(DaxWriter writer)
    {
        if (Definitions.Count > 0)
        {
            writer.Append("DEFINE");

            foreach (DaxVarDefinition definition in Definitions)
            {
                writer.Append("\n    ");
                definition.Write(writer);
            }

            writer.Append('\n');
        }

        writer.Append("EVALUATE\n").Write(Source);

        if (OrderBy.Count == 0)
            return;

        writer.Append("\nORDER BY ");

        for (int i = 0; i < OrderBy.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            OrderBy[i].WriteAsClause(writer);
        }
    }
}
