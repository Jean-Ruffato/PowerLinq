namespace PowerLinq.DaxConverter.Attributes;

/// <summary>
/// Maps a C# property to a DAX column reference in the format Table[Column].
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class DaxColumnAttribute(string columnReference) : Attribute
{
    /// <summary>The declared reference, in the form <c>Table[Column]</c> or <c>[Alias]</c>.</summary>
    public string ColumnReference { get; } = columnReference;
}
