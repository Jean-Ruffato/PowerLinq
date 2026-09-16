using System.Collections.Frozen;
using System.Data;
using System.Globalization;
using System.Reflection;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Mapping;

namespace PowerLinq.Tests.Mapping;

/// <summary>
/// Materialization: type conversion and culture independence.
/// </summary>
/// <remarks>
/// Several of these tests <b>would fail</b> before the fix. The culture ones run under pt-BR on
/// purpose, because that is exactly where the decimal separator and the day/month order diverge
/// from the invariant — and the old <c>Convert.ChangeType</c> without a culture read
/// <c>"1234.56"</c> as 123,456 under pt-BR.
/// </remarks>
public sealed class MaterializationTests
{
    private enum Status
    {
        Pendente = 0,
        Aprovado = 1,
        Recusado = 2
    }

    private sealed class Registro
    {
        [DaxColumn("[Valor]")] public decimal Valor { get; set; }
        [DaxColumn("[Quantidade]")] public int Quantidade { get; set; }
        [DaxColumn("[Data]")] public DateTime Data { get; set; }
        [DaxColumn("[Situacao]")] public Status Situacao { get; set; }
        [DaxColumn("[Identificador]")] public Guid Identificador { get; set; }
        [DaxColumn("[Competencia]")] public DateOnly Competencia { get; set; }
        [DaxColumn("[Abertura]")] public TimeOnly Abertura { get; set; }
        [DaxColumn("[Duracao]")] public TimeSpan Duracao { get; set; }
        [DaxColumn("[Registrado]")] public DateTimeOffset Registrado { get; set; }
        [DaxColumn("[ValorOpcional]")] public decimal? ValorOpcional { get; set; }
        [DaxColumn("[SituacaoOpcional]")] public Status? SituacaoOpcional { get; set; }
        [DaxColumn("[Texto]")] public string Texto { get; set; } = "";
    }

    private sealed class ComTipoSemSuporte
    {
        [DaxColumn("[Versao]")] public Version? Versao { get; set; }
    }

    // ---------- infrastructure ----------

    private static void UnderCulture(string culture, Action action)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            // CurrentCulture is per thread, so this does not escape into other tests.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static T ViaDaxRow<T>(Dictionary<string, object?> cells) where T : class =>
        EntityMapper.MapRow<T>(new DaxRow(cells), EntityMapper.GetColumnMappings(typeof(T)));

    private static T ViaDataRecord<T>(Dictionary<string, object?> cells) where T : class =>
        EntityMapper.MapRow<T>(new FakeDataRecord(cells), EntityMapper.GetColumnMappings(typeof(T)));

    /// <summary>Runs the same expectation on both read paths.</summary>
    private static void BothPaths<T>(Dictionary<string, object?> cells, Action<T> assert)
        where T : class
    {
        assert(ViaDaxRow<T>(cells));
        assert(ViaDataRecord<T>(cells));
    }

    // ---------- culture ----------

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void DecimalFromString_IsCultureIndependent(string culture) =>
        UnderCulture(culture, () => BothPaths<Registro>(
            new() { ["[Valor]"] = "1234.56" },
            r => Assert.Equal(1234.56m, r.Valor)));

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public void IsoDateFromString_IsCultureIndependent(string culture) =>
        UnderCulture(culture, () => BothPaths<Registro>(
            new() { ["[Data]"] = "2026-08-03" },
            r => Assert.Equal(new DateTime(2026, 8, 3), r.Data)));

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public void AmbiguousDateFromString_ReadsAsInvariantMonthFirst(string culture) =>
        // "03/08/2026" is 3 August under pt-BR and 8 March under en-US. The read is always the
        // invariant one — month first — so as not to depend on the process's culture.
        UnderCulture(culture, () => BothPaths<Registro>(
            new() { ["[Data]"] = "03/08/2026" },
            r => Assert.Equal(new DateTime(2026, 3, 8), r.Data)));

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public void DaxRowGetters_AreCultureIndependent(string culture) =>
        UnderCulture(culture, () =>
        {
            var row = new DaxRow(new Dictionary<string, object?>
            {
                ["dec"] = "1234.56",
                ["int"] = "42",
                ["long"] = "9007199254740993",
                ["date"] = "2026-08-03T10:30:00Z"
            });

            Assert.Equal(1234.56m, row.GetDecimal("dec"));
            Assert.Equal(42, row.GetInt("int"));
            Assert.Equal(9007199254740993L, row.GetLong("long"));
            Assert.Equal(new DateTime(2026, 8, 3, 10, 30, 0, DateTimeKind.Utc), row.GetDate("date"));
        });

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public void DaxRowGetString_UsesInvariantCulture(string culture) =>
        UnderCulture(culture, () =>
        {
            var row = new DaxRow(new Dictionary<string, object?> { ["v"] = 1234.56m });

            Assert.Equal("1234.56", row.GetString("v"));
        });

    // ---------- types ----------

    [Fact]
    public void Enum_FromNumericValue() =>
        BothPaths<Registro>(
            new() { ["[Situacao]"] = 2 },
            r => Assert.Equal(Status.Recusado, r.Situacao));

    [Fact]
    public void Enum_FromName() =>
        BothPaths<Registro>(
            new() { ["[Situacao]"] = "Aprovado" },
            r => Assert.Equal(Status.Aprovado, r.Situacao));

    [Fact]
    public void Enum_FromNameIgnoringCase() =>
        BothPaths<Registro>(
            new() { ["[Situacao]"] = "aprovado" },
            r => Assert.Equal(Status.Aprovado, r.Situacao));

    [Fact]
    public void NullableEnum_FromNumericValue() =>
        BothPaths<Registro>(
            new() { ["[SituacaoOpcional]"] = 1L },
            r => Assert.Equal(Status.Aprovado, r.SituacaoOpcional));

    [Fact]
    public void Guid_FromString()
    {
        var expected = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

        BothPaths<Registro>(
            new() { ["[Identificador]"] = expected.ToString() },
            r => Assert.Equal(expected, r.Identificador));
    }

    [Fact]
    public void Guid_FromGuid()
    {
        Guid expected = Guid.NewGuid();

        BothPaths<Registro>(
            new() { ["[Identificador]"] = expected },
            r => Assert.Equal(expected, r.Identificador));
    }

    [Fact]
    public void DateOnly_FromDateTime() =>
        BothPaths<Registro>(
            new() { ["[Competencia]"] = new DateTime(2026, 8, 3, 14, 0, 0) },
            r => Assert.Equal(new DateOnly(2026, 8, 3), r.Competencia));

    [Fact]
    public void DateOnly_FromIsoString() =>
        BothPaths<Registro>(
            new() { ["[Competencia]"] = "2026-08-03" },
            r => Assert.Equal(new DateOnly(2026, 8, 3), r.Competencia));

    [Fact]
    public void TimeOnly_FromTimeSpan() =>
        BothPaths<Registro>(
            new() { ["[Abertura]"] = new TimeSpan(8, 30, 0) },
            r => Assert.Equal(new TimeOnly(8, 30), r.Abertura));

    [Fact]
    public void TimeSpan_FromString() =>
        BothPaths<Registro>(
            new() { ["[Duracao]"] = "01:45:00" },
            r => Assert.Equal(new TimeSpan(1, 45, 0), r.Duracao));

    [Fact]
    public void TimeSpan_FromTimeSpan() =>
        BothPaths<Registro>(
            new() { ["[Duracao]"] = new TimeSpan(2, 0, 0) },
            r => Assert.Equal(new TimeSpan(2, 0, 0), r.Duracao));

    [Fact]
    public void DateTimeOffset_FromDateTime() =>
        BothPaths<Registro>(
            new() { ["[Registrado]"] = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc) },
            r => Assert.Equal(2026, r.Registrado.Year));

    [Fact]
    public void DateTimeOffset_FromIsoString() =>
        BothPaths<Registro>(
            new() { ["[Registrado]"] = "2026-08-03T12:00:00+00:00" },
            r => Assert.Equal(
                new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero), r.Registrado));

    [Fact]
    public void DateTimeOffset_FromAnotherDateLikeValue_GoesThroughToDateTime() =>
        BothPaths<Registro>(
            new() { ["[Registrado]"] = new DateOnly(2026, 8, 3) },
            r =>
            {
                Assert.Equal(2026, r.Registrado.Year);
                Assert.Equal(8, r.Registrado.Month);
                Assert.Equal(3, r.Registrado.Day);
            });

    [Fact]
    public void DateTime_FromDateTimeOffset_IsNormalizedToUtc() =>
        BothPaths<Registro>(
            new() { ["[Data]"] = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.FromHours(-3)) },
            r => Assert.Equal(new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc), r.Data));

    [Fact]
    public void DateTime_FromDateOnly() =>
        BothPaths<Registro>(
            new() { ["[Data]"] = new DateOnly(2026, 8, 3) },
            r => Assert.Equal(new DateTime(2026, 8, 3), r.Data));

    [Fact]
    public void Guid_FromByteArray()
    {
        var expected = Guid.NewGuid();

        BothPaths<Registro>(
            new() { ["[Identificador]"] = expected.ToByteArray() },
            r => Assert.Equal(expected, r.Identificador));
    }

    [Fact]
    public void Guid_FromAnUnsupportedValue_ThrowsNamingPropertyAndColumn()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ViaDaxRow<Registro>(new() { ["[Identificador]"] = 123 }));

        Assert.Contains("Registro.Identificador", ex.Message);
        Assert.Contains("[Identificador]", ex.Message);
    }

    [Fact]
    public void NullableDecimal_FromString() =>
        BothPaths<Registro>(
            new() { ["[ValorOpcional]"] = "9.99" },
            r => Assert.Equal(9.99m, r.ValorOpcional));

    [Fact]
    public void String_PassesThroughUnchanged() =>
        BothPaths<Registro>(
            new() { ["[Texto]"] = "Ordem de Venda" },
            r => Assert.Equal("Ordem de Venda", r.Texto));

    [Fact]
    public void MissingColumn_LeavesDefault() =>
        BothPaths<Registro>(
            new() { ["[Valor]"] = 10m },
            r =>
            {
                Assert.Equal(10m, r.Valor);
                Assert.Equal(Status.Pendente, r.Situacao);
                Assert.Null(r.ValorOpcional);
            });

    [Fact]
    public void NullCell_LeavesDefault() =>
        BothPaths<Registro>(
            new() { ["[Valor]"] = null, ["[ValorOpcional]"] = null },
            r =>
            {
                Assert.Equal(0m, r.Valor);
                Assert.Null(r.ValorOpcional);
            });

    // ---------- the constructor path through IDataRecord ----------

    private sealed record RegistroImutavel(
        [property: DaxColumn("[Valor]")] decimal Valor,
        [property: DaxColumn("[Quantidade]")] int Quantidade,
        [property: DaxColumn("[Texto]")] string Texto);

    [Fact]
    public void ConstructorPath_MaterializesFromADataRecord()
    {
        RegistroImutavel r = ViaDataRecord<RegistroImutavel>(
            new() { ["[Valor]"] = "12.5", ["[Quantidade]"] = 3, ["[Texto]"] = "Nota" });

        Assert.Equal(12.5m, r.Valor);
        Assert.Equal(3, r.Quantidade);
        Assert.Equal("Nota", r.Texto);
    }

    [Fact]
    public void ConstructorPath_FromDataRecord_LeavesAbsentAndNullColumnsAtDefault()
    {
        RegistroImutavel r = ViaDataRecord<RegistroImutavel>(
            new() { ["[Quantidade]"] = null, ["[Texto]"] = "Só o texto" });

        Assert.Equal(0m, r.Valor);
        Assert.Equal(0, r.Quantidade);
        Assert.Equal("Só o texto", r.Texto);
    }

    // ---------- error messages ----------

    [Fact]
    public void UnsupportedType_ThrowsNamingPropertyColumnAndType()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => ViaDaxRow<ComTipoSemSuporte>(new() { ["[Versao]"] = "1.0.0" }));

        Assert.Contains("Version", ex.Message);
        Assert.Contains("ComTipoSemSuporte.Versao", ex.Message);
        Assert.Contains("[Versao]", ex.Message);
    }

    [Fact]
    public void UnconvertibleValue_ThrowsNamingPropertyColumnAndType()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ViaDaxRow<Registro>(new() { ["[Quantidade]"] = "nao e numero" }));

        Assert.Contains("Registro.Quantidade", ex.Message);
        Assert.Contains("[Quantidade]", ex.Message);
        Assert.Contains("Int32", ex.Message);
    }

    [Fact]
    public void ConversionError_IsLocalized()
    {
        var portuguese = new ResourceManagerPowerLinqLocalizer("pt-BR");
        FrozenDictionary<string, DaxColumnMapping> mappings = EntityMapper.GetColumnMappings(typeof(Registro));
        var row = new DaxRow(new Dictionary<string, object?> { ["[Quantidade]"] = "x" });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => EntityMapper.MapRow<Registro>(row, mappings, portuguese));

        Assert.Contains("Não foi possível materializar", ex.Message);
    }

    /// <summary>A minimal IDataRecord, to exercise the direct executor's path.</summary>
    private sealed class FakeDataRecord(Dictionary<string, object?> data) : IDataRecord
    {
        private readonly List<string> _keys = [.. data.Keys];

        public int FieldCount => _keys.Count;
        public string GetName(int i) => _keys[i];
        public object GetValue(int i) => data[_keys[i]]!;
        public bool IsDBNull(int i) => data[_keys[i]] is null;

        public object this[int i] => GetValue(i);
        public object this[string name] => data[name]!;
        public bool GetBoolean(int i) => (bool)GetValue(i);
        public byte GetByte(int i) => (byte)GetValue(i);
        public long GetBytes(int i, long fo, byte[]? buf, int bo, int len) => 0;
        public char GetChar(int i) => (char)GetValue(i);
        public long GetChars(int i, long fo, char[]? buf, int bo, int len) => 0;
        public IDataReader GetData(int i) => throw new NotSupportedException();
        public string GetDataTypeName(int i) => GetValue(i).GetType().Name;
        public DateTime GetDateTime(int i) => (DateTime)GetValue(i);
        public decimal GetDecimal(int i) => (decimal)GetValue(i);
        public double GetDouble(int i) => (double)GetValue(i);
        public Type GetFieldType(int i) => GetValue(i).GetType();
        public float GetFloat(int i) => (float)GetValue(i);
        public Guid GetGuid(int i) => (Guid)GetValue(i);
        public short GetInt16(int i) => (short)GetValue(i);
        public int GetInt32(int i) => (int)GetValue(i);
        public long GetInt64(int i) => (long)GetValue(i);
        public int GetOrdinal(string name) => _keys.IndexOf(name);
        public string GetString(int i) => (string)GetValue(i);
        public int GetValues(object[] values) => 0;
    }
}
