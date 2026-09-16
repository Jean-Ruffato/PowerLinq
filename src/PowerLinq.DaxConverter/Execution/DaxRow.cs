using System.Globalization;
using PowerLinq.DaxConverter.Localization;

namespace PowerLinq.DaxConverter.Execution;

/// <summary>
/// One row of a DAX result, with typed and <b>defensive</b> reads.
/// </summary>
/// <remarks>
/// <para>
/// No getter throws. The semantics are uniform: a missing or null cell becomes <c>null</c> in the
/// nullable form and the default value in the non-nullable one; <c>NaN</c> and infinity — which
/// measures do produce, for example from a <c>DIVIDE</c> by zero — become <c>null</c>/zero; so do
/// overflow and an invalid format.
/// </para>
/// <para>
/// The choice is deliberate, and its cost is worth knowing: <b>a value that cannot be converted
/// becomes indistinguishable from a missing cell</b>. The gain is that a measure returning
/// infinity on one row does not bring down the reading of the whole report. When that distinction
/// matters, use <see cref="Cells"/> and decide for yourself.
/// </para>
/// <para>
/// Every conversion uses <see cref="CultureInfo.InvariantCulture"/>. The format of the values
/// arriving from XMLA belongs to the protocol, not to the process's preference — and carelessness
/// here is silent: a <c>TryParse</c> without a culture does not fail on an ambiguous value, it
/// merely interprets it differently.
/// </para>
/// </remarks>
public sealed class DaxRow
{
    private readonly IReadOnlyDictionary<string, object?> _cells;
    private readonly IPowerLinqLocalizer _localizer;

    /// <summary>Creates the row with error messages in English.</summary>
    public DaxRow(IReadOnlyDictionary<string, object?> cells)
        : this(cells, ResourceManagerPowerLinqLocalizer.English) { }

    /// <summary>Creates the row with the given language for the error messages.</summary>
    public DaxRow(
        IReadOnlyDictionary<string, object?> cells,
        IPowerLinqLocalizer localizer)
    {
        _cells = cells;
        _localizer = localizer;
    }

    /// <summary>The column's raw value, or <see langword="null"/> when it is not in the result.</summary>
    public object? this[string key] => _cells.TryGetValue(key, out object? value) ? value : null;

    /// <summary>This row's raw cells (column -> value). Exposed for interop with layers that still
    /// work with dictionaries (adapting to a legacy result type, for example).</summary>
    public IReadOnlyDictionary<string, object?> Cells => _cells;

    /// <summary>Typed read by column key, with the same defensive semantics as the getters below.</summary>
    public T Get<T>(string key) => ConvertCell<T>(key);

    /// <summary>The column's text, or <see langword="null"/> when the cell is null or missing.</summary>
    public string? GetString(string key)
    {
        object? value = this[key];
        return value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    /// <summary>64-bit integer; <c>0</c> when missing, null or not convertible.</summary>
    public long GetLong(string key) => GetNullableLong(key) ?? 0L;

    /// <summary>64-bit integer, or <see langword="null"/> when missing or not convertible.</summary>
    public long? GetNullableLong(string key)
    {
        object? value = this[key];
        if (value is null)
            return null;

        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>32-bit integer; <c>0</c> when missing, null or not convertible.</summary>
    public int GetInt(string key) => GetNullableInt(key) ?? 0;

    /// <summary>32-bit integer, or <see langword="null"/> when missing or not convertible.</summary>
    public int? GetNullableInt(string key)
    {
        object? value = this[key];
        if (value is null)
            return null;

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Decimal; <c>0</c> when missing, null, <c>NaN</c>, infinite or out of range.</summary>
    public decimal GetDecimal(string key) => GetNullableDecimal(key) ?? 0m;

    /// <summary>
    /// Decimal, or <see langword="null"/> when missing, <c>NaN</c>, infinite or out of range.
    /// </summary>
    /// <remarks>
    /// <c>NaN</c> and infinity are handled before the conversion because measures produce them — a
    /// <c>DIVIDE</c> by zero is the common case — and <see cref="Convert.ToDecimal(object)"/> would
    /// throw.
    /// </remarks>
    public decimal? GetNullableDecimal(string key)
    {
        object? value = this[key];
        if (value is null)
            return null;

        if (value is double d && (double.IsNaN(d) || double.IsInfinity(d)))
            return null;

        if (value is float f && (float.IsNaN(f) || float.IsInfinity(f)))
            return null;

        // Finite doubles/floats come through here too: a double beyond the decimal range
        // (double.MaxValue, for example) overflows in the cast — the catch turns that into null,
        // never an exception.
        try
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Date and time, or <see langword="null"/> when missing or not interpretable.
    /// </summary>
    /// <remarks>
    /// Text is read in the invariant culture — month first — and ISO 8601 preserves the declared
    /// <c>Kind</c>. A <see cref="DateTimeOffset"/> is normalized to UTC.
    /// </remarks>
    public DateTime? GetDate(string key)
    {
        object? value = this[key];
        if (value is null)
            return null;

        return value switch
        {
            DateTime dateTime => dateTime,
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,

            // Invariant culture, explicitly: a TryParse without a culture does not fail on an
            // ambiguous value, it merely interprets it differently — "03/08/2026" would be 3
            // August under pt-BR and 8 March under en-US, with no signal at all. Here it is
            // always the invariant reading (month first), and ISO 8601 preserves the declared Kind.
            _ => DateTime.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTime parsed)
                ? parsed
                : null
        };
    }

    private T ConvertCell<T>(string key)
    {
        Type type = typeof(T);

        if (type == typeof(string))
            return (T)(object?)GetString(key)!;
        if (type == typeof(long))
            return (T)(object)GetLong(key);
        if (type == typeof(long?))
            return (T)(object?)GetNullableLong(key)!;
        if (type == typeof(int))
            return (T)(object)GetInt(key);
        if (type == typeof(int?))
            return (T)(object?)GetNullableInt(key)!;
        if (type == typeof(decimal))
            return (T)(object)GetDecimal(key);
        if (type == typeof(decimal?))
            return (T)(object?)GetNullableDecimal(key)!;
        if (type == typeof(DateTime))
            return (T)(object)(GetDate(key) ?? default);
        if (type == typeof(DateTime?))
            return (T)(object?)GetDate(key)!;

        throw new NotSupportedException(
            _localizer.Format("TypedRowTypeUnsupported", type.Name));
    }
}
