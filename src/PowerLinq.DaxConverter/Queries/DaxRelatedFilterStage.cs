using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// A filter over columns of <b>another</b> table of the model, applied as filter context.
/// </summary>
/// <param name="TableName">The table the predicate applies to.</param>
/// <param name="Predicate">The predicate, already translated into the DAX tree.</param>
/// <remarks>
/// Kept apart from <see cref="DaxFilterStage"/> because the difference is not one of content but of
/// form: the entity's own predicate becomes the source (<c>FILTER(table, ...)</c>), while another
/// table's is only valid as filter context — an argument of <c>CALCULATETABLE</c>. Merging them
/// into a single predicate produced DAX the server rejects.
/// </remarks>
public sealed record DaxRelatedFilterStage(string TableName, IDaxExpression Predicate) : DaxStage;
