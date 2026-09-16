using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// A filter over columns of the entity's <b>own</b> table — what <c>FILTER</c> can iterate.
/// </summary>
/// <param name="Predicate">The predicate, already translated into the DAX tree.</param>
public sealed record DaxFilterStage(IDaxExpression Predicate) : DaxStage;
