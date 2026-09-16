namespace PowerLinq.DaxConverter.Attributes;

/// <summary>
/// Marks a property as a <b>navigation</b> to another entity of the model, rather than a data
/// column.
/// </summary>
/// <param name="foreignKey">
/// The local column that links the two tables — the foreign key on the <i>many</i> side. Optional:
/// it does not appear in the generated DAX, and serves the check against the model's relationships.
/// </param>
/// <remarks>
/// <para>
/// <b>It exists so a relationship stops being a convention inside a string.</b> Today a column from
/// another table goes in as a qualified textual reference —
/// <c>[DaxColumn("'DIM_EMPRESA'[CNPJ_RAIZ]")]</c> — which works and erases the most important piece
/// of information in a star model: that there is a relationship there. With navigation,
/// <c>r.Exporter.CnpjRoot</c> becomes writable.
/// </para>
/// <para>
/// <b>It is explicit, not inferred from the property's type.</b> Inferring "every property whose
/// type has <c>[DaxTable]</c> is a navigation" would turn a mistake — a property that happens to be
/// of an entity type — into a silent navigation. The explicit path was chosen for the first
/// version, and this is it.
/// </para>
/// <para>
/// <b>The key is optional because DAX does not use it.</b> <c>RELATED</c> resolves through the
/// <b>model's</b> relationship, not through a key the contract declares — declaring it here does
/// not change the generated DAX. It is there so the check against the schema
/// (<c>DaxContractValidator</c>) can point at the right relationship when there is more than one
/// path between the two tables.
/// </para>
/// <para>
/// <b>Navigation is not the way to filter by a dimension.</b> For that, the right tool is still the
/// filter table argument — a <c>Where</c> over a column of another table, which becomes
/// <c>CALCULATETABLE</c>. See the notes on <c>DaxRelated</c> for the two reasons: direction and
/// cost.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DaxNavigationAttribute(string? foreignKey = null) : Attribute
{
    /// <summary>The local column that links the tables, when declared.</summary>
    public string? ForeignKey { get; } = foreignKey;
}
