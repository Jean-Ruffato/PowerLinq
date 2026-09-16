namespace PowerLinq.DaxConverter.Model;

/// <summary>A column of the model.</summary>
/// <param name="Name">The column's name, as the model exposes it.</param>
/// <param name="DataType">The type, as the model declares it.</param>
/// <param name="IsHidden">Whether the column is hidden from report clients.</param>
public sealed record DaxSchemaColumn(string Name, string DataType, bool IsHidden);
