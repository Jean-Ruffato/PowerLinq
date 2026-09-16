namespace PowerLinq.DaxConverter.Model;

/// <summary>A table of the model, with its columns.</summary>
/// <param name="Name">The table's name.</param>
/// <param name="Columns">The columns.</param>
/// <param name="IsHidden">Whether the table is hidden from report clients.</param>
public sealed record DaxSchemaTable(
    string Name,
    IReadOnlyList<DaxSchemaColumn> Columns,
    bool IsHidden);
