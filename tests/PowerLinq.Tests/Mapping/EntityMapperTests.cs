using System.Collections.Frozen;
using System.Data;
using System.Reflection;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Mapping;

namespace PowerLinq.Tests.Mapping;

public class EntityMapperTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[VendaID]")]
        public int VendaId { get; set; }

        [DaxColumn("Venda[Cliente]")]
        public string Cliente { get; set; } = string.Empty;

        [DaxColumn("Venda[Valor]")]
        public decimal Valor { get; set; }
    }

    private sealed class SemAtributo
    {
        public int Id { get; set; }
    }

    [Fact]
    public void GetTableName_WithAttribute_ReturnsAttributeValue()
    {
        Assert.Equal("Venda", EntityMapper.GetTableName<Venda>());
    }

    [Fact]
    public void GetTableName_WithoutAttribute_ReturnsClassName()
    {
        Assert.Equal("SemAtributo", EntityMapper.GetTableName<SemAtributo>());
    }

    [Fact]
    public void GetColumnMappings_ReturnsMappedProperties()
    {
        FrozenDictionary<string, DaxColumnMapping> mappings = EntityMapper.GetColumnMappings(typeof(Venda));

        Assert.Equal(3, mappings.Count);
        Assert.True(mappings.ContainsKey("Venda[VendaID]"));
        Assert.True(mappings.ContainsKey("Venda[Cliente]"));
        Assert.True(mappings.ContainsKey("Venda[Valor]"));
    }

    [Fact]
    public void MapRow_MapsColumnsToProperties()
    {
        FrozenDictionary<string, DaxColumnMapping> mappings = EntityMapper.GetColumnMappings(typeof(Venda));
        IDataRecord record = new FakeDataRecord(new()
        {
            ["Venda[VendaID]"] = 99,
            ["Venda[Cliente]"] = "João",
            ["Venda[Valor]"] = 250.50m
        });

        Venda venda = EntityMapper.MapRow<Venda>(record, mappings);

        Assert.Equal(99, venda.VendaId);
        Assert.Equal("João", venda.Cliente);
        Assert.Equal(250.50m, venda.Valor);
    }

    // Minimal IDataRecord fake for unit tests
    private sealed class FakeDataRecord(Dictionary<string, object?> data) : IDataRecord
    {
        private readonly List<string> _keys = [.. data.Keys];

        public int FieldCount => _keys.Count;
        public string GetName(int i) => _keys[i];
        public object GetValue(int i) => data[_keys[i]]!;
        public bool IsDBNull(int i) => data[_keys[i]] is null;

        // Unused IDataRecord members
        public object this[int i] => GetValue(i);
        public object this[string name] => data[name]!;
        public bool GetBoolean(int i) => (bool)GetValue(i);
        public byte GetByte(int i) => (byte)GetValue(i);
        public long GetBytes(int i, long fo, byte[]? buf, int bo, int len) => 0;
        public char GetChar(int i) => (char)GetValue(i);
        public long GetChars(int i, long fo, char[]? buf, int bo, int len) => 0;
        public IDataReader GetData(int i) => throw new NotImplementedException();
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
