namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// An operator applied to the query, at the position it was applied.
/// </summary>
/// <remarks>
/// <para>
/// There is a single reason for this hierarchy to exist: <b>here the order is data</b>. The
/// previous representation was a flat record that kept <c>Filter</c>, <c>TopN</c>, <c>Skip</c> and
/// <c>OrderBy</c> as independent fields, so <c>Where(p).Take(5)</c> and <c>Take(5).Where(p)</c>
/// produce exactly the same value — although they mean different things. In a list of stages they
/// are two different lists, and the generated DAX differs.
/// </para>
/// <para>
/// Not every stage wraps the table. <see cref="DaxOrderStage"/> generates nothing on its own: in
/// DAX ordering is an <b>argument</b> of <c>TOPN</c>, and it only survives as the
/// <c>EVALUATE</c>'s <c>ORDER BY</c> clause when no window consumes it. See
/// <c>DaxPipelineBuilder</c> for how that is folded.
/// </para>
/// </remarks>
public abstract record DaxStage;
