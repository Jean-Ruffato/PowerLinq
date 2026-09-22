namespace PowerLinq.DaxConverter.Model;

/// <remarks>
/// A type separate from <see cref="DaxModelSchema"/> because the file format and the in-memory
/// model change for different reasons: the first has compatibility to maintain, the second has
/// an index and lookup methods that do not belong in a file.
/// </remarks>
internal sealed record Document(
    int FormatVersion,
    IReadOnlyList<DaxSchemaTable>? Tables,
    IReadOnlyList<DaxSchemaMeasure>? Measures,
    IReadOnlyList<DaxSchemaRelationship>? Relationships);
