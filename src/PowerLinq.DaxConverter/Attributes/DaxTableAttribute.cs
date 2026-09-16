namespace PowerLinq.DaxConverter.Attributes;

/// <summary>
/// Name of the entity's DAX table. When absent, the type's own name is used.
/// </summary>
/// <remarks>
/// A name with a space or a special character is quoted on write, as DAX requires — there is no
/// need to write the quotes here, and a name that is already quoted is preserved.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DaxTableAttribute(string tableName) : Attribute
{
    /// <summary>The declared name.</summary>
    public string TableName { get; } = tableName;
}
