namespace PowerLinq.DaxConverter.Model;

/// <summary>A measure of the model.</summary>
/// <param name="Name">The measure's name, without brackets.</param>
/// <param name="Table">The table it is declared on.</param>
/// <param name="DataType">The result's type, as the model declares it.</param>
/// <param name="IsHidden">Whether the measure is hidden from report clients.</param>
/// <remarks>
/// The name comes <b>without</b> brackets, because that is how the model stores it. Whoever writes
/// DAX adds them, and <c>MeasureAsync</c> accepts both forms — keeping the model's form avoids
/// deciding here which of the two is canonical.
/// </remarks>
public sealed record DaxSchemaMeasure(string Name, string Table, string DataType, bool IsHidden);
