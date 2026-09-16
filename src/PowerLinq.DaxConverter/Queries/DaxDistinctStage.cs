namespace PowerLinq.DaxConverter.Queries;

/// <summary>Removes duplicate rows — <c>DISTINCT(source)</c>.</summary>
/// <remarks>
/// <para>
/// It does not change the result's set of columns: only the rows. That is why it can appear after a
/// projection without affecting how the following operators resolve columns.
/// </para>
/// <para>
/// <c>DISTINCT</c> over a table expression, not <c>VALUES</c> over a column: <c>VALUES</c> adds the
/// blank row when there is a referential integrity violation in the model, which would give a
/// filter's option list an unexplained empty item.
/// </para>
/// </remarks>
public sealed record DaxDistinctStage : DaxStage;
