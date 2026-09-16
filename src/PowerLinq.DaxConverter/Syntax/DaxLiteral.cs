using System.Globalization;

namespace PowerLinq.DaxConverter.Syntax;

/// <summary>Entry point for turning a .NET value into the matching DAX literal node.</summary>
public static class DaxLiteral
{
    /// <summary>
    /// Picks the literal node that corresponds to a .NET value.
    /// </summary>
    /// <remarks>
    /// This is the only place that inspects the value's type, and it runs once per node
    /// construction — not on every render. After that each literal knows its own syntax and writes
    /// it without deciding anything.
    /// </remarks>
    public static IDaxExpression From(object? value) => value switch
    {
        null => DaxBlankLiteral.Instance,

        string s => new DaxTextLiteral(s),
        char c => new DaxTextLiteral(c.ToString()),

        bool b => b ? DaxBooleanLiteral.True : DaxBooleanLiteral.False,

        DateTime dt => new DaxDateLiteral(dt.Year, dt.Month, dt.Day),
        DateTimeOffset dto => new DaxDateLiteral(dto.Year, dto.Month, dto.Day),
        DateOnly date => new DaxDateLiteral(date.Year, date.Month, date.Day),

        // Enums normally map to numeric columns, which is what EntityMapper's Convert.ChangeType
        // already assumes.
        Enum e => new DaxNumberLiteral(Convert.ToInt64(e, CultureInfo.InvariantCulture)),

        byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal => new DaxNumberLiteral((IFormattable)value),

        // Unknown types become text: a bare token would be a syntax error, whereas a string is at
        // worst a type mismatch.
        _ => new DaxTextLiteral(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
    };
}
