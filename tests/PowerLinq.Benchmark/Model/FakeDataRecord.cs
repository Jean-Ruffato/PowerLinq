using System.Data;

namespace PowerLinq.Benchmark.Model;

/// <summary>
/// An in-memory <see cref="IDataRecord"/>. It isolates <c>EntityMapper.MapRow</c> from the cost of
/// any ADOMD driver — what is left is pure reflection. The column names follow the format XMLA
/// returns (<c>Table[Column]</c>), which is the key <c>EntityMapper.GetColumnMappings</c> uses.
/// </summary>
public sealed class FakeDataRecord(string[] names, object?[] values) : IDataRecord
{
    public int FieldCount => names.Length;

    public string GetName(int i) => names[i];

    public bool IsDBNull(int i) => values[i] is null or DBNull;

    public object GetValue(int i) => values[i]!;

    public int GetOrdinal(string name) => Array.IndexOf(names, name);

    public Type GetFieldType(int i) => values[i]?.GetType() ?? typeof(object);

    public string GetDataTypeName(int i) => GetFieldType(i).Name;

    public object this[int i] => GetValue(i);

    public object this[string name] => GetValue(GetOrdinal(name));

    public int GetValues(object[] target)
    {
        int count = Math.Min(target.Length, values.Length);
        for (int i = 0; i < count; i++)
            target[i] = values[i]!;

        return count;
    }

    public bool GetBoolean(int i) => (bool)GetValue(i);
    public byte GetByte(int i) => (byte)GetValue(i);
    public char GetChar(int i) => (char)GetValue(i);
    public DateTime GetDateTime(int i) => (DateTime)GetValue(i);
    public decimal GetDecimal(int i) => (decimal)GetValue(i);
    public double GetDouble(int i) => (double)GetValue(i);
    public float GetFloat(int i) => (float)GetValue(i);
    public Guid GetGuid(int i) => (Guid)GetValue(i);
    public short GetInt16(int i) => (short)GetValue(i);
    public int GetInt32(int i) => (int)GetValue(i);
    public long GetInt64(int i) => (long)GetValue(i);
    public string GetString(int i) => (string)GetValue(i);

    public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException();

    public long GetChars(int i, long fieldOffset, char[]? buffer, int bufferOffset, int length)
        => throw new NotSupportedException();

    public IDataReader GetData(int i) => throw new NotSupportedException();

    /// <summary>Builds a realistic row, varying the values by index.</summary>
    public static FakeDataRecord CreateProdutoRow(int seed) => new(
        [
            "Produto[ProdutoID]",
            "Produto[Nome]",
            "Produto[Categoria]",
            "Produto[Preco]",
            "Produto[Quantidade]",
            "Produto[Ativo]",
            "Produto[DataCadastro]",
            "Produto[Desconto]",
            "Produto[Observacao]" // unmapped: exercises the discard path
        ],
        [
            seed,
            $"Produto {seed}",
            seed % 2 == 0 ? "Eletrônicos" : "Móveis",
            10.5m + seed,
            seed % 100,
            seed % 3 != 0,
            new DateTime(2024, 1, 1).AddDays(seed % 365),
            seed % 5 == 0 ? null : 0.15m,
            "ignorado"
        ]);
}
