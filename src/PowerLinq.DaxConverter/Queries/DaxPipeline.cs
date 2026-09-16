namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// A query's accumulated state as an <b>ordered sequence</b> of operators.
/// </summary>
/// <remarks>
/// <para>
/// It is the query's internal representation. It replaced a flat record — <c>Filter</c>,
/// <c>TopN</c>, <c>Skip</c> and <c>OrderBy</c> as independent fields — and the swap solved three
/// things at once, all of them caused by the flat definition's lack of order:
/// </para>
/// <list type="bullet">
/// <item>
/// compositions outside the canonical order stop being <b>unrepresentable</b>. They were refused
/// because the structure did not distinguish them, not because DAX cannot express them;
/// </item>
/// <item>
/// an ordering declared before a grouping stops being silently discarded, because its position in
/// the sequence is known;
/// </item>
/// <item>
/// the terminals can carry the query instead of ready-made DAX, which is what allows composing
/// after <c>Select</c>, <c>Aggregate</c> or <c>Join</c>.
/// </item>
/// </list>
/// <para>
/// Generating DAX is a function of the pipeline — see <c>DaxPipelineBuilder</c>.
/// </para>
/// </remarks>
/// <param name="TableName">The query's root table, already quoted as DAX requires.</param>
/// <param name="EntityType">The entity's type, for materializing the result.</param>
public sealed record DaxPipeline(string TableName, Type EntityType)
{
    /// <summary>The operators, in the order they were applied.</summary>
    public IReadOnlyList<DaxStage> Stages { get; init; } = [];

    /// <summary>Returns a new pipeline with <paramref name="stage"/> appended.</summary>
    public DaxPipeline Then(DaxStage stage) => this with { Stages = [.. Stages, stage] };

    /// <summary>
    /// The column references the result carries, or <see langword="null"/> when the shape is still
    /// the table's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="null"/> and an empty list mean different things: <see langword="null"/> is
    /// "any column of the table will do, resolve through the entity's mapping"; a list is the
    /// <b>closed</b> set of columns the last reshaping stage left available.
    /// </para>
    /// <para>
    /// It is what allows ordering by an extension column after aggregating —
    /// <c>ORDER BY [Total]</c> — instead of refusing. Without it, resolution would use the source
    /// entity's mapping and emit <c>Venda[Total]</c>, a reference <c>SUMMARIZECOLUMNS</c> does not
    /// produce.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string>? ResultColumns()
    {
        IReadOnlyList<string>? columns = null;

        foreach (DaxStage stage in Stages)
            columns = ColumnsAfter(stage, columns);

        return columns;
    }

    /// <summary>
    /// The columns the result carries <b>after</b> a stage, given what it carried before.
    /// <c>null</c> passes through: a stage that does not reshape does not change the set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is the only place that knows this.</b> The rule used to exist in two: here, serving
    /// composition, and in the <c>available</c> accumulator of <c>DaxPipelineBuilder</c>'s fold,
    /// serving the writing. Both listed the same three reshapes with the same expressions, and they
    /// diverged the first time one gained a new case — the subtotal column went into the fold only,
    /// and <c>Where(r =&gt; !r.IsTotal)</c> started refusing a column the DAX does return.
    /// </para>
    /// <para>
    /// A grouping key comes out with the table's reference (<c>Venda[Categoria]</c>) and an
    /// extension with the name in brackets (<c>[Total]</c>) — which is the form
    /// <c>SUMMARIZECOLUMNS</c> produces.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string>? ColumnsAfter(
        DaxStage stage,
        IReadOnlyList<string>? current) =>
        stage switch
        {
            DaxProjectStage project => [.. project.Columns.Select(column => $"[{column.Name}]")],

            DaxGroupStage group =>
            [
                .. group.Keys.Select(key => key.Reference),
                .. group.Extensions.Select(extension => $"[{extension.Name}]"),
                .. group.RollupLevels.Select(level => $"[{level.FlagName}]")
            ],

            DaxJoinStage join => [.. join.Projections.Select(p => $"[{p.OutputName}]")],

            _ => current
        };
}
