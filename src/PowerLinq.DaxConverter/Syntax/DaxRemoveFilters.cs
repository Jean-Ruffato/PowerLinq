namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// <c>REMOVEFILTERS(target...)</c> — it <b>removes</b> filters from the context, it does not apply
/// them.
/// </summary>
/// <param name="Targets">
/// The target references: columns (<c>Venda[Uf]</c>) or a table (<c>Venda</c>), as DAX writes
/// them. Empty is not representable — see the remarks.
/// </param>
/// <remarks>
/// <para>
/// <b>It is the modifier, not a filter.</b> <c>REMOVEFILTERS</c> is the modern synonym of
/// <c>ALL</c> used inside <c>CALCULATE</c>, and the library emits this name because it <i>says</i>
/// what it does: <c>ALL</c> has two readings — "the whole table" and "remove the filters" — and
/// the second is the one that holds in this position. In DAX that someone else will read, the name
/// that needs no footnote is the better one.
/// </para>
/// <para>
/// An empty target would generate <c>REMOVEFILTERS()</c>, which removes the filter from
/// <b>everything</b> — including tables the author never mentioned. That is far too powerful to
/// come out of an omitted argument, and whoever builds the node resolves it beforehand by passing
/// the table.
/// </para>
/// </remarks>
public sealed record DaxRemoveFilters(IReadOnlyList<string> Targets) : IDaxTableExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer)
    {
        writer.Append("REMOVEFILTERS(");

        for (int i = 0; i < Targets.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            writer.Append(Targets[i]);
        }

        writer.Append(')');
    }
}
