using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Builders;

/// <summary>What the fold produces. A record, not a tuple: there are six things.</summary>
/// <param name="Source">The table expression that was built.</param>
/// <param name="Pending">The ordering terms no window consumed.</param>
/// <param name="Definitions">The declarations of the <c>DEFINE</c> block.</param>
/// <param name="BeforeWindow">The source as it stood before the first window, or null.</param>
/// <param name="FilterTables">The filters in filter-context argument form.</param>
/// <param name="Reshaped">Whether some stage changed the result's shape.</param>
internal sealed record Folded(
    IDaxTableExpression Source,
    List<DaxOrderTerm> Pending,
    List<DaxVarDefinition> Definitions,
    IDaxTableExpression? BeforeWindow,
    List<IDaxTableExpression> FilterTables,
    bool Reshaped);
