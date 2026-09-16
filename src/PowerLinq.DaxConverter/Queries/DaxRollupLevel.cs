using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// One rollup level inside <c>ROLLUPADDISSUBTOTAL</c>: a subset of the grouping keys, plus the name
/// of the column that marks that level's total row.
/// </summary>
/// <param name="Keys">
/// This level's columns — a subset of <see cref="DaxGroupStage.Keys"/>, in the order they enter
/// <c>ROLLUPGROUP</c>.
/// </param>
/// <param name="FlagName">
/// The flag column's name, without brackets — as <c>ROLLUPADDISSUBTOTAL</c> receives it. The
/// reference to it in the result carries brackets: <c>[FlagName]</c>.
/// </param>
/// <remarks>
/// <para>
/// <b>The order of the levels in <see cref="DaxGroupStage.RollupLevels"/> is the hierarchy</b>, not
/// a loose list: <c>ROLLUPADDISSUBTOTAL(level1, "flag1", level2, "flag2")</c> aggregates as a
/// hierarchy, not as a cartesian product. With two levels (period, exporter) the rows that come
/// back are only three — the cell (period × exporter), the per-period total (period open, exporter
/// closed) and the grand total (both closed). The "exporter open, period closed" combination
/// <b>does not exist</b>: asking for that level through the flag returns an empty list, with no
/// error. Swapping the levels' order changes what each flag combination means.
/// </para>
/// </remarks>
public sealed record DaxRollupLevel(IReadOnlyList<DaxColumnRef> Keys, string FlagName);
