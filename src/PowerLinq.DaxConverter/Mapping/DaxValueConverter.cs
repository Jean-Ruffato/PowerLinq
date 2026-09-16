using System.Globalization;
using System.Reflection;
using PowerLinq.DaxConverter.Localization;

namespace PowerLinq.DaxConverter.Mapping;

/// <summary>
/// Converts a raw value coming from a DAX result into the type of the destination property.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariant culture, always.</b> The format of the values arriving from the XMLA endpoint
/// belongs to the protocol, not to the user's preference, so the conversion never depends on the
/// process's culture. On the read side this mirrors the discipline
/// <see cref="Syntax.DaxNumberLiteral"/> already applies on the write side — and the carelessness
/// was real: <c>DateTime.Parse</c> without a culture <b>does not fail</b> on an ambiguous value,
/// it merely interprets it differently. <c>"03/08/2026"</c> is 3 August under pt-BR and 8 March
/// under en-US.
/// </para>
/// <para>
/// <b>Beyond what <c>Convert.ChangeType</c> reaches.</b> That method only handles
/// <see cref="IConvertible"/>, so <c>enum</c>, <see cref="Guid"/>, <see cref="DateOnly"/>,
/// <see cref="TimeOnly"/> and <see cref="TimeSpan"/> blew up with an
/// <see cref="InvalidCastException"/> — without saying which property caused it. An entity with an
/// enum property filtered correctly and then failed while reading the result.
/// </para>
/// </remarks>
internal static class DaxValueConverter
{
    /// <summary>
    /// Converts <paramref name="value"/> to <paramref name="targetType"/>, which must already
    /// arrive stripped of <see cref="Nullable{T}"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The destination type has no conversion path.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The value exists but could not be converted to the destination type.
    /// </exception>
    /// <remarks>
    /// The type check comes <b>before</b> <c>Describe</c>, and not inside the overload below,
    /// because <c>Describe</c> is an argument — it would be evaluated on every cell to compose a
    /// description only the error message uses. On a read of 10,000 rows with 8 mapped columns
    /// that is 80,000 interpolated strings, all discarded. Measured: materialization dropped from
    /// 605 B to 112 B per row — 6.05 MB to 1.12 MB on that read.
    /// </remarks>
    public static object Convert(
        object value,
        Type targetType,
        PropertyInfo property,
        string columnReference,
        IPowerLinqLocalizer localizer) =>
        targetType.IsInstanceOfType(value)
            ? value
            : Convert(value, targetType, Describe(property), columnReference, localizer);

    /// <summary>
    /// The same conversion, for a destination that is not an entity property — the result of a
    /// scalar aggregate, for example. <paramref name="target"/> goes into the error message in
    /// place of <c>Type.Property</c>.
    /// </summary>
    /// <remarks>
    /// The overload exists so the scalar path does not get a type table of its own. Without it,
    /// <c>SumAsync&lt;decimal&gt;</c> would have to repeat here what row materialization already
    /// knows how to do — and the two lists would diverge the first time one of them gained a new
    /// type.
    /// </remarks>
    public static object Convert(
        object value,
        Type targetType,
        string target,
        string columnReference,
        IPowerLinqLocalizer localizer)
    {
        // Already of the expected type: nothing to convert, and nothing to get wrong.
        if (targetType.IsInstanceOfType(value))
            return value;

        try
        {
            return ConvertCore(value, targetType, target, columnReference, localizer);
        }
        catch (Exception ex) when (ex is InvalidCastException
                                      or FormatException
                                      or OverflowException
                                      or ArgumentException)
        {
            throw new InvalidOperationException(
                localizer.Format(
                    "MaterializationConversionFailed",
                    target,
                    columnReference,
                    targetType.Name,
                    value.GetType().Name),
                ex);
        }
    }

    private static object ConvertCore(
        object value,
        Type targetType,
        string target,
        string columnReference,
        IPowerLinqLocalizer localizer)
    {
        if (targetType.IsEnum)
            return ConvertEnum(value, targetType);

        if (targetType == typeof(Guid))
            return ConvertGuid(value);

        if (targetType == typeof(DateOnly))
            return DateOnly.FromDateTime(ToDateTime(value));

        if (targetType == typeof(TimeOnly))
        {
            return value is TimeSpan span
                ? TimeOnly.FromTimeSpan(span)
                : TimeOnly.FromDateTime(ToDateTime(value));
        }

        if (targetType == typeof(DateTimeOffset))
        {
            return value switch
            {
                DateTime dateTime => new DateTimeOffset(dateTime),
                string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, Styles),
                _ => new DateTimeOffset(ToDateTime(value))
            };
        }

        if (targetType == typeof(TimeSpan))
        {
            return value is string timeText
                ? TimeSpan.Parse(timeText, CultureInfo.InvariantCulture)
                : throw Unsupported(targetType, target, columnReference, localizer);
        }

        if (targetType == typeof(DateTime))
            return ToDateTime(value);

        // The general path covers numerics, bool, char and string. The culture matters here:
        // without it, "1.234,56" and "1,234.56" swap meanings depending on the process.
        //
        // The support decision looks at the DESTINATION TYPE, not at the value. Testing
        // `value is IConvertible` classified it wrongly: a string is IConvertible, so converting
        // to an unsupported type (Version, for example) entered here, failed, and the error came
        // out as "conversion failed" instead of "type not supported". GetTypeCode returns Object
        // for everything that is not a primitive, a string or a DateTime.
        if (Type.GetTypeCode(targetType) != TypeCode.Object)
            return System.Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);

        throw Unsupported(targetType, target, columnReference, localizer);
    }

    /// <summary>
    /// An enum arrives as a number (the common case, numeric columns) or as the label in text,
    /// which some models return. Both forms are accepted.
    /// </summary>
    private static object ConvertEnum(object value, Type targetType) =>
        value is string text
            ? Enum.Parse(targetType, text, ignoreCase: true)
            : Enum.ToObject(
                targetType,
                System.Convert.ChangeType(value, Enum.GetUnderlyingType(targetType), CultureInfo.InvariantCulture));

    private static object ConvertGuid(object value) => value switch
    {
        string text => Guid.Parse(text),
        byte[] bytes => new Guid(bytes),
        _ => throw new InvalidCastException()
    };

    private static DateTime ToDateTime(object value) => value switch
    {
        DateTime dateTime => dateTime,
        DateTimeOffset offset => offset.UtcDateTime,
        DateOnly date => date.ToDateTime(TimeOnly.MinValue),
        string text => DateTime.Parse(text, CultureInfo.InvariantCulture, Styles),
        _ => System.Convert.ToDateTime(value, CultureInfo.InvariantCulture)
    };

    /// <summary>
    /// <see cref="DateTimeStyles.RoundtripKind"/> preserves the <c>Kind</c> declared in the text
    /// (the <c>Z</c> or the offset of ISO 8601), instead of assuming local time.
    /// </summary>
    internal const DateTimeStyles Styles = DateTimeStyles.RoundtripKind;

    private static NotSupportedException Unsupported(
        Type targetType,
        string target,
        string columnReference,
        IPowerLinqLocalizer localizer) =>
        new(localizer.Format(
            "MaterializationTypeUnsupported",
            targetType.Name,
            target,
            columnReference));

    private static string Describe(PropertyInfo property) =>
        $"{property.DeclaringType?.Name}.{property.Name}";
}
