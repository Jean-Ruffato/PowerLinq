using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Queries;

/// <summary>One column of a projection's result: the output name and the expression that produces it.</summary>
/// <param name="Name">The column's name in the result, without the brackets.</param>
/// <param name="Expression">The expression evaluated per row.</param>
public sealed record DaxProjectionColumn(string Name, IDaxExpression Expression);
