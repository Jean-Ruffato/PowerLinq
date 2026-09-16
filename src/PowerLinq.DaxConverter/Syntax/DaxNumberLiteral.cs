using System.Globalization;

namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// A DAX number. The value is preserved as an <see cref="IFormattable"/> so the tree stays
/// inspectable, and it is formatted in <see cref="CultureInfo.InvariantCulture"/> on write: in
/// cultures such as pt-BR the decimal separator would be a comma, which in DAX separates
/// arguments.
/// </summary>
public sealed record DaxNumberLiteral(IFormattable Value) : IDaxExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer) =>
        writer.Append(Value.ToString(null, CultureInfo.InvariantCulture));
}
