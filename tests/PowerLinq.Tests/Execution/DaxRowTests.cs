using System.Globalization;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Localization;

namespace PowerLinq.Tests.Execution;

/// <summary>
/// <see cref="DaxRow"/>'s defensive reading.
/// </summary>
/// <remarks>
/// The contract is that no getter throws: a missing, null or unconvertible cell becomes
/// <c>null</c> in the nullable form and the default in the non-nullable one. <c>NaN</c>, infinity,
/// overflow and an invalid format all fall into the same bucket. These edge cases — the ones that
/// hold up the promise that "a measure returning infinity on one row does not bring down the whole
/// report" — were not exercised by any dedicated test.
/// </remarks>
public sealed class DaxRowTests
{
    private static DaxRow Row(params (string Key, object? Value)[] cells) =>
        new(cells.ToDictionary(c => c.Key, c => c.Value));

    // ---------- indexer and raw cells ----------

    [Fact]
    public void Indexer_ReturnsRawValue_WhenPresent()
    {
        DaxRow row = Row(("[V]", 42));

        Assert.Equal(42, row["[V]"]);
    }

    [Fact]
    public void Indexer_ReturnsNull_WhenKeyIsAbsent()
    {
        DaxRow row = Row(("[V]", 42));

        Assert.Null(row["ausente"]);
    }

    [Fact]
    public void Cells_ExposesTheUnderlyingDictionary()
    {
        var source = new Dictionary<string, object?> { ["[A]"] = 1, ["[B]"] = null };
        var row = new DaxRow(source);

        Assert.Equal(source, row.Cells);
    }

    // ---------- GetString ----------

    [Fact]
    public void GetString_ReturnsNull_WhenAbsentOrNull()
    {
        DaxRow row = Row(("[V]", null));

        Assert.Null(row.GetString("[V]"));
        Assert.Null(row.GetString("ausente"));
    }

    [Fact]
    public void GetString_ConvertsNonStringValues_InvariantCulture()
    {
        DaxRow row = Row(("[V]", 1234.5m));

        Assert.Equal("1234.5", row.GetString("[V]"));
    }

    // ---------- GetLong / GetNullableLong ----------

    [Fact]
    public void GetLong_ReturnsZero_WhenAbsentNullOrUnconvertible()
    {
        Assert.Equal(0L, Row(("[V]", null)).GetLong("[V]"));
        Assert.Equal(0L, Row(("[V]", 1)).GetLong("ausente"));
        Assert.Equal(0L, Row(("[V]", "não é número")).GetLong("[V]"));
    }

    [Fact]
    public void GetLong_ConvertsFromStringAndDouble()
    {
        Assert.Equal(42L, Row(("[V]", "42")).GetLong("[V]"));
        Assert.Equal(3L, Row(("[V]", 3.0d)).GetLong("[V]"));
    }

    [Fact]
    public void GetNullableLong_ReturnsNull_OnFormatOverflowAndInvalidCast()
    {
        Assert.Null(Row(("[V]", null)).GetNullableLong("[V]"));
        Assert.Null(Row(("[V]", "abc")).GetNullableLong("[V]"));
        Assert.Null(Row(("[V]", double.MaxValue)).GetNullableLong("[V]"));
        Assert.Null(Row(("[V]", new object())).GetNullableLong("[V]"));
    }

    // ---------- GetInt / GetNullableInt ----------

    [Fact]
    public void GetInt_ReturnsZero_WhenAbsentNullOrUnconvertible()
    {
        Assert.Equal(0, Row(("[V]", null)).GetInt("[V]"));
        Assert.Equal(0, Row(("[V]", 1)).GetInt("ausente"));
        Assert.Equal(0, Row(("[V]", "x")).GetInt("[V]"));
    }

    [Fact]
    public void GetInt_ConvertsFromString()
    {
        Assert.Equal(7, Row(("[V]", "7")).GetInt("[V]"));
    }

    [Fact]
    public void GetNullableInt_ReturnsNull_OnOverflowFormatAndInvalidCast()
    {
        Assert.Null(Row(("[V]", null)).GetNullableInt("[V]"));
        Assert.Null(Row(("[V]", long.MaxValue)).GetNullableInt("[V]"));
        Assert.Null(Row(("[V]", "abc")).GetNullableInt("[V]"));
        Assert.Null(Row(("[V]", new object())).GetNullableInt("[V]"));
    }

    // ---------- GetDecimal / GetNullableDecimal ----------

    [Fact]
    public void GetDecimal_ReturnsZero_WhenAbsentNullNaNInfinityOrOutOfRange()
    {
        Assert.Equal(0m, Row(("[V]", null)).GetDecimal("[V]"));
        Assert.Equal(0m, Row(("[V]", double.NaN)).GetDecimal("[V]"));
        Assert.Equal(0m, Row(("[V]", double.PositiveInfinity)).GetDecimal("[V]"));
        Assert.Equal(0m, Row(("[V]", double.MaxValue)).GetDecimal("[V]"));
    }

    [Fact]
    public void GetNullableDecimal_ReturnsNull_ForNaNAndInfinity_DoubleAndFloat()
    {
        Assert.Null(Row(("[V]", double.NaN)).GetNullableDecimal("[V]"));
        Assert.Null(Row(("[V]", double.NegativeInfinity)).GetNullableDecimal("[V]"));
        Assert.Null(Row(("[V]", float.NaN)).GetNullableDecimal("[V]"));
        Assert.Null(Row(("[V]", float.PositiveInfinity)).GetNullableDecimal("[V]"));
    }

    [Fact]
    public void GetNullableDecimal_ReturnsNull_WhenFiniteButBeyondDecimalRange()
    {
        Assert.Null(Row(("[V]", double.MaxValue)).GetNullableDecimal("[V]"));
        Assert.Null(Row(("[V]", "não é número")).GetNullableDecimal("[V]"));
        Assert.Null(Row(("[V]", new object())).GetNullableDecimal("[V]"));
    }

    [Fact]
    public void GetNullableDecimal_ConvertsFiniteValues()
    {
        Assert.Equal(2.5m, Row(("[V]", 2.5d)).GetNullableDecimal("[V]"));
        Assert.Equal(10m, Row(("[V]", "10")).GetNullableDecimal("[V]"));
    }

    // ---------- GetDate ----------

    [Fact]
    public void GetDate_ReturnsNull_WhenAbsentOrUnparseable()
    {
        Assert.Null(Row(("[V]", null)).GetDate("[V]"));
        Assert.Null(Row(("[V]", "não é data")).GetDate("[V]"));
        Assert.Null(Row(("[V]", new object())).GetDate("[V]"));
    }

    [Fact]
    public void GetDate_PassesThroughDateTime()
    {
        var when = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(when, Row(("[V]", when)).GetDate("[V]"));
    }

    [Fact]
    public void GetDate_NormalizesDateTimeOffsetToUtc()
    {
        var offset = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.FromHours(-3));

        Assert.Equal(offset.UtcDateTime, Row(("[V]", offset)).GetDate("[V]"));
        Assert.Equal(new DateTime(2026, 8, 30, 15, 0, 0, DateTimeKind.Utc), Row(("[V]", offset)).GetDate("[V]"));
    }

    [Fact]
    public void GetDate_ParsesIso8601PreservingKind()
    {
        DateTime? parsed = Row(("[V]", "2026-08-30T10:30:00Z")).GetDate("[V]");

        Assert.Equal(new DateTime(2026, 8, 30, 10, 30, 0, DateTimeKind.Utc), parsed);
        Assert.Equal(DateTimeKind.Utc, parsed!.Value.Kind);
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public void GetDate_ReadsAmbiguousTextAsInvariantMonthFirst(string culture)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            // "03/08/2026": 3 August under pt-BR, 8 March under en-US. The read is always invariant.
            Assert.Equal(new DateTime(2026, 3, 8), Row(("[V]", "03/08/2026")).GetDate("[V]"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ---------- Get<T> ----------

    [Fact]
    public void GenericGet_DispatchesToEachTypedGetter()
    {
        DaxRow row = Row(
            ("s", 10),
            ("l", "20"),
            ("i", "30"),
            ("dec", "40.5"),
            ("date", "2026-08-30T00:00:00Z"));

        Assert.Equal("10", row.Get<string>("s"));
        Assert.Equal(20L, row.Get<long>("l"));
        Assert.Equal((long?)20L, row.Get<long?>("l"));
        Assert.Equal(30, row.Get<int>("i"));
        Assert.Equal((int?)30, row.Get<int?>("i"));
        Assert.Equal(40.5m, row.Get<decimal>("dec"));
        Assert.Equal((decimal?)40.5m, row.Get<decimal?>("dec"));
        Assert.Equal(new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc), row.Get<DateTime>("date"));
        Assert.Equal((DateTime?)new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc), row.Get<DateTime?>("date"));
    }

    [Fact]
    public void GenericGet_ReturnsDefaults_WhenCellIsAbsent()
    {
        DaxRow row = Row(("outra", 1));

        Assert.Null(row.Get<string>("x"));
        Assert.Equal(0L, row.Get<long>("x"));
        Assert.Null(row.Get<long?>("x"));
        Assert.Equal(0, row.Get<int>("x"));
        Assert.Null(row.Get<int?>("x"));
        Assert.Equal(0m, row.Get<decimal>("x"));
        Assert.Null(row.Get<decimal?>("x"));
        Assert.Equal(default, row.Get<DateTime>("x"));
        Assert.Null(row.Get<DateTime?>("x"));
    }

    [Fact]
    public void GenericGet_ThrowsNotSupported_ForAnUnhandledType()
    {
        DaxRow row = Row(("[V]", 1));

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => row.Get<Guid>("[V]"));

        Assert.Contains(nameof(Guid), ex.Message);
    }

    [Fact]
    public void GenericGet_UnsupportedTypeMessage_IsLocalized()
    {
        var row = new DaxRow(
            new Dictionary<string, object?> { ["[V]"] = 1 },
            new ResourceManagerPowerLinqLocalizer("pt-BR"));

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => row.Get<Guid>("[V]"));

        Assert.Contains("não é suportado", ex.Message);
    }
}
