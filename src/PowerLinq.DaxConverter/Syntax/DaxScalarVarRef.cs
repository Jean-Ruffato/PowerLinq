namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// The same thing, for a <c>VAR</c> that holds a scalar — pagination's total count.
/// </summary>
/// <remarks>
/// A type of its own rather than <see cref="DaxVarRef"/> serving both: in DAX the reference is
/// just the name, but the split between scalar and table is the one the rest of the tree makes — a
/// <see cref="DaxFilter"/> only accepts a table as its source, and that restriction is checked by
/// the compiler. A node that was both would hand the check back to the server.
/// </remarks>
public sealed record DaxScalarVarRef(string Name) : IDaxExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer) => writer.Append(Name);
}
