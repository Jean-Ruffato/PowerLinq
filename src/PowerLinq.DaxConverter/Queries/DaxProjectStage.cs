namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// Projects onto another contract — <c>SELECTCOLUMNS(source, "name", expression, ...)</c>.
/// </summary>
/// <param name="Columns">The result's columns, in declared order.</param>
/// <remarks>
/// <para>
/// After this stage the rows <b>change shape</b>: the columns come to be referenced as
/// <c>[Name]</c>, not as <c>Table[Column]</c>. That is what prevents, for now, composing operators
/// that resolve columns — <c>Where</c>, <c>OrderBy</c>, <c>SumAsync</c> — after a projection:
/// resolution still uses the source entity's mapping. The operators that need no column reference
/// do work.
/// </para>
/// <para>
/// While the projection was a separate terminal class, that limitation was total: there was no
/// operator at all after it beyond <c>ToListAsync</c> and <c>FirstOrDefaultAsync</c>.
/// </para>
/// </remarks>
public sealed record DaxProjectStage(IReadOnlyList<DaxProjectionColumn> Columns) : DaxStage;
