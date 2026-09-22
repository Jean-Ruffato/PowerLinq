using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Builders;

/// <summary>What the rewrite produced, or why it produced nothing.</summary>
/// <param name="Expression">The rewritten expression; null when it failed.</param>
/// <param name="UnmappedColumn">
/// The column reference the map does not cover — the reshape does not carry it to the result.
/// </param>
/// <param name="UnsupportedNode">The DAX of the node the traversal cannot cross.</param>
/// <remarks>
/// The two failure reasons are distinct for the caller: a column left out is a choice made by
/// whoever wrote the query, and has an obvious fix (include the column, or order afterwards); a
/// node that cannot be crossed is a limitation of this class, and the message must not suggest
/// the query is at fault.
/// </remarks>
internal sealed record Result(
    IDaxExpression? Expression,
    string? UnmappedColumn,
    string? UnsupportedNode);
