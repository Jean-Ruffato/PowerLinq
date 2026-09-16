namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// <c>ALLEXCEPT(table, columns...)</c> — removes the filter from the whole table <b>except</b> the
/// listed columns.
/// </summary>
/// <param name="Table">The table whose filter is removed.</param>
/// <param name="Columns">The columns whose filter <b>stays</b>.</param>
/// <remarks>
/// <para>
/// The list is of what <b>survives</b>, which is the opposite of what a naive reading of the name
/// suggests. That is why the API exposes it as "keep the filter on these columns only" instead of
/// repeating <c>AllExcept</c>: the DAX name describes the mechanics, the API name describes the
/// intent.
/// </para>
/// <para>
/// With no columns at all it is equivalent to <c>REMOVEFILTERS(table)</c>, and there the right node
/// is the other one — the builder chooses, so the generated DAX does not carry a one-argument
/// <c>ALLEXCEPT</c> that only makes the reader wonder what was excepted.
/// </para>
/// </remarks>
public sealed record DaxAllExcept(string Table, IReadOnlyList<string> Columns) : IDaxTableExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer)
    {
        writer.Append("ALLEXCEPT(").Append(Table);

        foreach (string column in Columns)
            writer.Append(", ").Append(column);

        writer.Append(')');
    }
}
