namespace PowerLinq.DaxConverter.Model;

/// <summary>
/// A relationship of the model, with its cardinality and whether it is active.
/// </summary>
/// <param name="FromTable">The source table.</param>
/// <param name="FromColumn">The source column.</param>
/// <param name="FromCardinality">The cardinality of the source side.</param>
/// <param name="ToTable">The destination table.</param>
/// <param name="ToColumn">The destination column.</param>
/// <param name="ToCardinality">The cardinality of the destination side.</param>
/// <param name="IsActive">
/// Whether the relationship is the active path. An inactive one exists in the model but does not
/// filter — it is only used by <c>USERELATIONSHIP</c> inside a <c>CALCULATE</c>.
/// </param>
/// <remarks>
/// <b>Being active matters as much as existing.</b> An inactive relationship satisfies "there is a
/// path between the two tables" and does not satisfy "the filter crosses" — and that is exactly
/// the distinction needed to avoid picking a path silently.
/// </remarks>
public sealed record DaxSchemaRelationship(
    string FromTable,
    string FromColumn,
    DaxCardinality FromCardinality,
    string ToTable,
    string ToColumn,
    DaxCardinality ToCardinality,
    bool IsActive);
