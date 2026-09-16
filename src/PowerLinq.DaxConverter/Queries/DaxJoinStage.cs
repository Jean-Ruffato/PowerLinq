namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// Equijoin with another table — <c>SELECTCOLUMNS(GENERATE(outer, FILTER(inner, keys)), ...)</c>.
/// </summary>
/// <param name="InnerTableName">The inner side's table.</param>
/// <param name="InnerStages">
/// The inner side's stages — filters only. Empty when the inner side is the bare table.
/// </param>
/// <param name="OuterKeyReferences">The outer side's key columns, in declared order.</param>
/// <param name="InnerKeyReferences">The inner side's key columns, in the same order.</param>
/// <param name="Projections">The result's columns, in declared order.</param>
/// <remarks>
/// <para>
/// The <b>outer</b> side is the source accumulated up to this stage, not the bare table. While the
/// join was a terminal class created from an <c>IDaxTable</c>, the outer side could only be the
/// whole table — so there was no way to filter before joining, nor to chain one join over another.
/// As a stage, both become expressible.
/// </para>
/// <para>
/// The internal aliases (<c>__pl_outer_key_0</c>, <c>__pl_outer_0</c>, ...) exist because
/// <c>GENERATE</c> has no join clause: both tables are reduced to columns of known name, the
/// <c>FILTER</c> compares the key aliases, and the outer <c>SELECTCOLUMNS</c> renames onto the
/// output contract. With a composite key there is one alias pair per component, and the
/// <c>FILTER</c> compares them all with <c>&amp;&amp;</c>.
/// </para>
/// <para>
/// <see cref="InnerStages"/> only accepts filters. The inner side goes in as a <b>table to cross
/// with</b>, and ordering or limiting before the cross would change which rows take part in it
/// without the generated DAX expressing that — so it is refused during composition rather than
/// discarded.
/// </para>
/// </remarks>
public sealed record DaxJoinStage(
    string InnerTableName,
    IReadOnlyList<DaxStage> InnerStages,
    IReadOnlyList<string> OuterKeyReferences,
    IReadOnlyList<string> InnerKeyReferences,
    IReadOnlyList<DaxJoinProjection> Projections) : DaxStage;
