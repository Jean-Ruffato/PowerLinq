namespace PowerLinq.DaxConverter.Syntax;

/// <summary>A direct reference to a table of the model.</summary>
/// <remarks>
/// <paramref name="Name"/> keeps the name as declared; the single quotes DAX requires for a name
/// with a space or a special character are applied on write, by
/// <see cref="DaxIdentifier.Quote"/>.
/// </remarks>
public sealed record DaxTableRef(string Name) : IDaxTableExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer) => writer.Append(DaxIdentifier.Quote(Name));
}
