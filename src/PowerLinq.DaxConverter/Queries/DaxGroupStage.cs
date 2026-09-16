using PowerLinq.DaxConverter.Syntax;
using PowerLinq.DaxConverter.Translators;

namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// Groups and aggregates — <c>SUMMARIZECOLUMNS(keys..., filter tables..., "name", expression...)</c>.
/// </summary>
/// <param name="Keys">The grouping columns. Empty for a keyless aggregation.</param>
/// <param name="Extensions">The extension columns: name and aggregated expression.</param>
/// <remarks>
/// <para>
/// <b>This stage does not wrap the source</b>, and that is what sets it apart from all the others.
/// <c>FILTER</c>, <c>TOPN</c> and <c>SELECTCOLUMNS</c> take a table and return another, so folding
/// them is nesting them. <c>SUMMARIZECOLUMNS</c> does not: it takes <b>grouping columns</b> and
/// <b>filter tables</b> as sibling arguments, and there is no position where the accumulated table
/// fits.
/// </para>
/// <para>
/// The practical consequence is the refusal to group after a <c>Take</c> or a <c>Skip</c>: the
/// window cannot be expressed as a filter table without changing the meaning, and passing it that
/// way would discard it silently. The way forward would be materializing the window in a
/// <c>VAR</c> before grouping.
/// </para>
/// </remarks>
/// <param name="RollupLevels">
/// The rollup levels, in the order they enter <c>ROLLUPADDISSUBTOTAL</c>; empty when the result
/// carries no subtotal row. A single level (the common case) covers the whole key in one
/// <c>ROLLUPGROUP</c> and returns one total row; more than one expresses a hierarchy — see
/// <see cref="DaxRollupLevel"/>.
/// </param>
public sealed record DaxGroupStage(
    IReadOnlyList<DaxColumnRef> Keys,
    IReadOnlyList<DaxAggregateColumn> Extensions,
    IReadOnlyList<DaxRollupLevel> RollupLevels) : DaxStage
{
    /// <summary>No rollup — the common case, and the only one before subtotals existed.</summary>
    public DaxGroupStage(IReadOnlyList<DaxColumnRef> keys, IReadOnlyList<DaxAggregateColumn> extensions)
        : this(keys, extensions, []) { }
}
