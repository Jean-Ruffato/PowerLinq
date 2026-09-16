namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// A parameter's place in the tree, where a literal would otherwise stand.
/// </summary>
/// <param name="Slot">The parameter's position in the list of values, zero-based.</param>
/// <remarks>
/// <para>
/// This is what makes it possible to cache the DAX of a query whose values change on every run.
/// Without it the captured value is folded into a <see cref="DaxTextLiteral"/> during translation,
/// and the resulting tree serves <b>one</b> value only — caching that would mean handing back the
/// DAX of a different filter, the correctness bug that is the central risk here.
/// </para>
/// <para>
/// <b>It writes no value at all.</b> It marks the boundary, and the writer cuts the text there —
/// see <see cref="DaxWriter.Placeholder"/>. A node that wrote the value could not be cached, which
/// is the whole reason it exists.
/// </para>
/// <para>
/// The slot is positional rather than named because binding is positional: whoever compiles the
/// query declares the parameters in the order the values are passed. Names would require a
/// dictionary per execution, and the order is already the contract.
/// </para>
/// </remarks>
public sealed record DaxParameterRef(int Slot) : IDaxExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer) => writer.Placeholder(Slot);
}
