using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Queries;

/// <summary>A column the join's result carries, and which side it comes from.</summary>
/// <param name="OutputName">The column's name in the result, without the brackets.</param>
/// <param name="FromOuter">
/// <see langword="true"/> when the column comes from the outer side; <see langword="false"/> from
/// the inner one.
/// </param>
/// <param name="Expression">
/// The expression that produces the column, evaluated <b>inside its own side</b>, before the cross.
/// Usually a column reference, but it may be computed.
/// </param>
/// <remarks>
/// Being evaluated before the cross is what confines the expression to <b>one</b> side: there each
/// side only has its own columns. Combining the two would require computing after the
/// <c>GENERATE</c>, referencing the aliases — and it is simpler to project the columns separately
/// and combine them in a <c>Select</c> after the join, which now exists.
/// </remarks>
public sealed record DaxJoinProjection(string OutputName, bool FromOuter, IDaxExpression Expression);
