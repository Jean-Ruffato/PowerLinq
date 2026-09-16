using System.Collections.Frozen;
using System.Linq.Expressions;
using PowerLinq.DaxConverter.Builders;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Mapping;
using PowerLinq.DaxConverter.Syntax;
using PowerLinq.DaxConverter.Translators;

namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// A query grouped by a key. The following <see cref="Select{TResult}"/> defines the aggregations
/// and materializes the whole thing as a <c>SUMMARIZECOLUMNS</c>.
/// </summary>
/// <remarks>
/// Each property of <see cref="Select{TResult}"/>'s result type maps, through
/// <see cref="Attributes.DaxColumnAttribute"/>, either to the key
/// (<c>[DaxColumn("Table[Column]")]</c>) or to an extension column (<c>[DaxColumn("[name]")]</c>).
/// </remarks>
public sealed class DaxGroupedQuery<T, TKey> where T : class
{
    private readonly IDaxQueryExecutor _executor;
    private readonly DaxPipeline _pipeline;
    private readonly Expression<Func<T, TKey>> _keySelector;
    private readonly IPowerLinqLocalizer _localizer;
    private readonly IReadOnlyList<RollupLevelRequest> _rollupLevels;

    internal DaxGroupedQuery(
        IDaxQueryExecutor executor,
        DaxPipeline pipeline,
        Expression<Func<T, TKey>> keySelector,
        IPowerLinqLocalizer localizer,
        IReadOnlyList<RollupLevelRequest>? rollupLevels = null)
    {
        _executor = executor;
        _pipeline = pipeline;
        _keySelector = keySelector;
        _localizer = localizer;
        _rollupLevels = rollupLevels ?? [];
    }

    /// <summary>A rollup level not yet translated: the key expression and the flag's name.</summary>
    internal sealed record RollupLevelRequest(Expression Keys, string FlagName);

    /// <summary>
    /// Asks for the <b>subtotal row</b> alongside the detail rows, in the same query, covering the
    /// whole key in a single level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It generates <c>ROLLUPADDISSUBTOTAL(ROLLUPGROUP(&lt;keys&gt;), "is_total")</c>. The detail
    /// rows and the total row come back together, and the <c>[is_total]</c> column says which is
    /// which — the pattern of any grid with a footer.
    /// </para>
    /// <para>
    /// <b>Summing the rows on the client is not equivalent.</b> It only coincides for an additive
    /// measure. For a <c>DISTINCTCOUNT</c>, an average or a ratio, the engine's total is computed
    /// in the total's filter context, and summing the rows gives the wrong number — plausible
    /// enough to pass review. And a client-side total requires fetching <b>every</b> row, which
    /// pagination prevents.
    /// </para>
    /// <para>
    /// The result contract <b>must</b> declare the column, with <c>[DaxColumn("[is_total]")]</c>.
    /// Without it the total row would arrive as a detail row with blank keys, indistinguishable —
    /// the worst possible failure here, because the number shows up duplicated and nobody sees an
    /// error.
    /// </para>
    /// <para>
    /// For a hierarchy of two or more levels — each with its own flag, in a single query — use the
    /// <see cref="WithSubtotal(string, Expression{Func{T, object}})"/> overload instead of this
    /// one. The two do not combine: calling this one after a level already exists is refused,
    /// because this one covers the whole key and the resulting level would be redundant with the
    /// previous one.
    /// </para>
    /// </remarks>
    public DaxGroupedQuery<T, TKey> WithSubtotal()
    {
        if (_rollupLevels.Count > 0)
            throw new NotSupportedException(_localizer.Get("SubtotalAlreadyLeveled"));

        return AddLevel(_keySelector.Body, DaxPipelineBuilder.DefaultSubtotalFlagName);
    }

    /// <summary>
    /// Asks for one <b>level</b> of hierarchical rollup: the total row of a subset of the key,
    /// distinguished by <paramref name="flagName"/>.
    /// </summary>
    /// <param name="flagName">
    /// The name of the column that marks this level's total row, without brackets — the result
    /// contract has to declare it with <c>[DaxColumn("[name]")]</c>, the same way
    /// <see cref="WithSubtotal()"/> requires for <c>[is_total]</c>.
    /// </param>
    /// <param name="levelKeys">
    /// A subset of the columns passed to <c>GroupBy</c> — <c>v =&gt; new { v.A, v.B }</c>, or a
    /// single column. Columns outside the grouping key are refused: <c>ROLLUPGROUP</c> only accepts
    /// what is already in the <c>SUMMARIZECOLUMNS</c>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The order of the calls is the hierarchy.</b> Each <c>WithSubtotal</c> appends a level to
    /// <c>ROLLUPADDISSUBTOTAL(level1, "flag1", level2, "flag2", ...)</c>, in the order it was
    /// called — the first call is the <b>outer</b> level of the hierarchy, the last is the
    /// <b>inner</b> one. Reversing the order of the calls changes what each flag combination means.
    /// </para>
    /// <para>
    /// <b>The hierarchy does not produce every flag combination.</b> With two levels — period
    /// (outer) and exporter (inner) — the rows that come back are only three:
    /// </para>
    /// <list type="table">
    /// <listheader><term>outer level's flag</term><description>inner level's flag — meaning</description></listheader>
    /// <item><term><c>FALSE</c></term><description><c>FALSE</c> — period × exporter cell (the full grain)</description></item>
    /// <item><term><c>FALSE</c></term><description><c>TRUE</c> — period total (exporter closed)</description></item>
    /// <item><term><c>TRUE</c></term><description><c>TRUE</c> — grand total (both closed)</description></item>
    /// </list>
    /// <para>
    /// The <c>TRUE, FALSE</c> combination — outer level closed with the inner one open — <b>does
    /// not exist</b>. Asking for that level (filtering by <c>PeriodoTotal &amp;&amp;
    /// !ExportadorTotal</c> expecting the per-exporter total with the period aggregated, for
    /// example) returns an <b>empty</b> list, with no error: if that level is needed, it requires a
    /// query of its own, with the levels' order reversed or with a separate grouping.
    /// </para>
    /// </remarks>
    public DaxGroupedQuery<T, TKey> WithSubtotal(string flagName, Expression<Func<T, object>> levelKeys)
    {
        if (string.IsNullOrWhiteSpace(flagName))
            throw new ArgumentException(_localizer.Get("SubtotalFlagNameRequired"), nameof(flagName));

        if (_rollupLevels.Any(level => level.FlagName == flagName))
            throw new NotSupportedException(_localizer.Format("SubtotalDuplicateFlag", flagName));

        EnsureLevelKeysAreWithinTheGroupKey(levelKeys.Body);

        return AddLevel(levelKeys.Body, flagName);
    }

    private DaxGroupedQuery<T, TKey> AddLevel(Expression keys, string flagName) =>
        new(_executor, _pipeline, _keySelector, _localizer, [.. _rollupLevels, new RollupLevelRequest(keys, flagName)]);

    /// <summary>
    /// Refuses a level whose columns are not part of the key passed to <c>GroupBy</c>.
    /// </summary>
    /// <remarks>
    /// Without this guard, <c>ROLLUPGROUP</c> would receive a column the <c>SUMMARIZECOLUMNS</c>
    /// does not even group by — DAX the server rejects, and only at execution time, not during
    /// composition.
    /// </remarks>
    private void EnsureLevelKeysAreWithinTheGroupKey(Expression levelKeys)
    {
        var groupKeyColumns = new HashSet<string>(
            DaxAggregationTranslator
                .TranslateKey(_keySelector.Body, _pipeline.TableName, _localizer, rowContext: false)
                .Select(column => column.Reference),
            StringComparer.Ordinal);

        foreach (DaxColumnRef column in DaxAggregationTranslator.TranslateKey(
            levelKeys, _pipeline.TableName, _localizer, rowContext: false))
        {
            if (!groupKeyColumns.Contains(column.Reference))
            {
                throw new NotSupportedException(
                    _localizer.Format("SubtotalLevelKeyNotInGroupKey", column.Reference));
            }
        }
    }

    /// <summary>Defines the group's aggregations, producing the <c>SUMMARIZECOLUMNS</c>.</summary>
    /// <remarks>
    /// It returns a <see cref="DaxQuery{TResult}"/>: the grouping is a pipeline stage, so the
    /// execution operators apply after it — there used to be two, <c>ToListAsync</c> and
    /// <c>FirstOrDefaultAsync</c>.
    /// </remarks>
    public DaxQuery<TResult> Select<TResult>(
        Expression<Func<IDaxGroup<T, TKey>, TResult>> resultSelector) where TResult : class
    {
        // rowContext: false because the key goes into the SUMMARIZECOLUMNS, which does NOT open a
        // row context — a navigation there comes out as a bare qualified reference, and RELATED
        // would be an error.
        List<DaxColumnRef> keyColumns = DaxAggregationTranslator.TranslateKey(
            _keySelector.Body,
            _pipeline.TableName,
            _localizer,
            rowContext: false);

        List<DaxAggregateColumn> extensions = DaxAggregationTranslator.TranslateProjection(
            resultSelector,
            _pipeline.TableName,
            _localizer);

        List<DaxRollupLevel> rollupLevels = [.. _rollupLevels.Select(level => new DaxRollupLevel(
            DaxAggregationTranslator.TranslateKey(
                level.Keys, _pipeline.TableName, _localizer, rowContext: false),
            level.FlagName))];

        if (rollupLevels.Count > 0)
            EnsureCarriesEveryRollupFlag(typeof(TResult), rollupLevels);

        DaxPipeline grouped = (_pipeline with { EntityType = typeof(TResult) })
            .Then(new DaxGroupStage(keyColumns, extensions, rollupLevels));

        return new DaxQuery<TResult>(_executor, grouped, _localizer);
    }

    /// <summary>
    /// Refuses a contract that does not declare the flag column of some rollup level.
    /// </summary>
    /// <remarks>
    /// Without this guard the total row would arrive as a detail row with blank keys: the grid
    /// would show an empty category carrying the total's value, and summing the column would give
    /// double. Materialization has no way to notice — it silently discards an unmapped column,
    /// which is the right behaviour for everything else.
    /// </remarks>
    private void EnsureCarriesEveryRollupFlag(Type result, IReadOnlyList<DaxRollupLevel> rollupLevels)
    {
        FrozenDictionary<string, DaxColumnMapping> mappings = EntityMapper.GetColumnMappings(result);

        foreach (DaxRollupLevel level in rollupLevels)
        {
            string flagColumn = $"[{level.FlagName}]";

            if (!mappings.ContainsKey(flagColumn))
            {
                throw new NotSupportedException(
                    _localizer.Format("SubtotalFlagRequired", result.Name, flagColumn));
            }
        }
    }
}
