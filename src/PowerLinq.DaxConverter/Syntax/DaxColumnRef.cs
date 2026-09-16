namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// Column reference already in DAX form (<c>Table[Column]</c>).
/// </summary>
/// <remarks>
/// The reference is kept whole rather than split into table + column because
/// <see cref="Attributes.DaxColumnAttribute"/> receives it ready-made from the user and it may
/// contain single-quoted names (<c>'Ordem de Venda'[Qtd]</c>). Reparsing that would introduce a
/// failure point with no gain today.
/// </remarks>
public sealed record DaxColumnRef(string Reference) : IDaxExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer) => writer.Append(Reference);
}
