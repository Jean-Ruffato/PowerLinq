using System.Collections.Frozen;
using System.Reflection;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Mapping;

namespace PowerLinq.Tests.Mapping;

/// <summary>
/// Per-type column mapping cache.
/// </summary>
/// <remarks>
/// Reflection ran on every query execution, at a <b>fixed</b> cost — irrelevant when diluted
/// across a large result, dominant in a query that brings back a single row. And single-row
/// queries are the majority on a dashboard.
/// </remarks>
public sealed class ColumnMappingCacheTests
{
    [DaxTable("Venda")]
    private sealed class Venda
    {
        [DaxColumn("Venda[VendaID]")] public int VendaId { get; set; }
        [DaxColumn("Venda[Valor]")] public decimal Valor { get; set; }
    }

    private sealed class SemAtributo
    {
        public int Id { get; set; }
        public string Nome { get; set; } = "";
    }

    [Fact]
    public void SecondCall_ReturnsTheVerySameInstance()
    {
        FrozenDictionary<string, DaxColumnMapping> first = EntityMapper.GetColumnMappings(typeof(Venda));
        FrozenDictionary<string, DaxColumnMapping> second = EntityMapper.GetColumnMappings(typeof(Venda));

        // Reference equality, not content: it is what proves reflection did not run again.
        Assert.Same(first, second);
    }

    [Fact]
    public void DifferentTypes_GetDifferentMappings()
    {
        FrozenDictionary<string, DaxColumnMapping> venda = EntityMapper.GetColumnMappings(typeof(Venda));
        FrozenDictionary<string, DaxColumnMapping> sem = EntityMapper.GetColumnMappings(typeof(SemAtributo));

        Assert.NotSame(venda, sem);
        Assert.True(venda.ContainsKey("Venda[VendaID]"));
        Assert.True(sem.ContainsKey("Nome"));
        Assert.False(sem.ContainsKey("Venda[VendaID]"));
    }

    /// <summary>
    /// What the cache makes necessary to prove: the value is shared across every query for the
    /// type, so mutation by the caller would corrupt everyone's mapping.
    /// <see cref="FrozenDictionary{TKey,TValue}"/> exposes no writes — the risk stops existing
    /// rather than depending on discipline.
    /// </summary>
    [Fact]
    public void TheReturnedMapping_IsNotAMutableDictionary()
    {
        object mappings = EntityMapper.GetColumnMappings(typeof(Venda));

        Assert.IsNotType<Dictionary<string, PropertyInfo>>(mappings);
        Assert.IsType<FrozenDictionary<string, DaxColumnMapping>>(mappings, exactMatch: false);
    }

    [Fact]
    public void ConcurrentResolution_OfManyTypes_StaysConsistent()
    {
        Type[] types =
        [
            typeof(Venda), typeof(SemAtributo), typeof(Outra1), typeof(Outra2),
            typeof(Outra3), typeof(Outra4), typeof(Outra5), typeof(Outra6)
        ];

        // One resolution per type before the threads would be the easy path, and would test
        // nothing: what matters is the race on the first resolution.
        var results = new FrozenDictionary<string, DaxColumnMapping>[64];

        Parallel.For(0, results.Length, index =>
            results[index] = EntityMapper.GetColumnMappings(types[index % types.Length]));

        for (int index = 0; index < results.Length; index++)
        {
            Assert.Same(
                EntityMapper.GetColumnMappings(types[index % types.Length]),
                results[index]);
        }
    }

    private sealed class Outra1 { public int A { get; set; } }
    private sealed class Outra2 { public int B { get; set; } }
    private sealed class Outra3 { public int C { get; set; } }
    private sealed class Outra4 { public int D { get; set; } }
    private sealed class Outra5 { public int E { get; set; } }
    private sealed class Outra6 { public int F { get; set; } }

    /// <summary>
    /// Materialization stays correct after the change of structure and the inversion of the loop in
    /// <c>MapRow(DaxRow, ...)</c>, which now walks the row's cells instead of the mapping's
    /// keys.
    /// </summary>
    [Fact]
    public void Materialization_StillMatchesEveryKeyForm()
    {
        FrozenDictionary<string, DaxColumnMapping> mappings = EntityMapper.GetColumnMappings(typeof(SemAtributo));

        // A property with no attribute registers four forms; any of them has to match.
        foreach (string key in new[] { "Id", "[Id]", "SemAtributo[Id]" })
        {
            var row = new DaxRow(new Dictionary<string, object?> { [key] = 7 });
            SemAtributo entity = EntityMapper.MapRow<SemAtributo>(row, mappings);

            Assert.Equal(7, entity.Id);
        }
    }

    // ---------- compiled setter ----------

    private sealed class ComSetterPrivado
    {
        public int Publico { get; set; }

        // CanWrite is true, but GetSetMethod() returns null: the compiled setter cannot call the
        // accessor, so this case falls back to reflection. If the fallback failed, the value would come back default.
        public string Restrito { get; private set; } = "";

        public void Definir(string valor) => Restrito = valor;
    }

    [Fact]
    public void CompiledSetter_FallsBackToReflectionForANonPublicSetter()
    {
        FrozenDictionary<string, DaxColumnMapping> mappings =
            EntityMapper.GetColumnMappings(typeof(ComSetterPrivado));
        var row = new DaxRow(new Dictionary<string, object?>
        {
            ["Publico"] = 5,
            ["Restrito"] = "escrito"
        });

        ComSetterPrivado entity = EntityMapper.MapRow<ComSetterPrivado>(row, mappings);

        Assert.Equal(5, entity.Publico);
        Assert.Equal("escrito", entity.Restrito);
    }

    private sealed class ComAnulaveis
    {
        public decimal? Valor { get; set; }
        public int? Quantidade { get; set; }
        public DateTime? Data { get; set; }
        public StatusPedido? Status { get; set; }
    }

    private enum StatusPedido
    {
        Aberto = 1,
        Fechado = 2
    }

    /// <summary>
    /// The case the compiled setter could break silently: the conversion returns the value boxed as
    /// the <b>underlying</b> type (<c>decimal</c>), and the property is
    /// <c>decimal?</c>. It is the unboxing inside the compiled lambda that has to accept that.
    /// </summary>
    [Fact]
    public void CompiledSetter_AssignsToNullableProperties()
    {
        FrozenDictionary<string, DaxColumnMapping> mappings =
            EntityMapper.GetColumnMappings(typeof(ComAnulaveis));
        var row = new DaxRow(new Dictionary<string, object?>
        {
            ["Valor"] = "1234.56",
            ["Quantidade"] = 7L,
            ["Data"] = "2026-08-05T00:00:00",
            ["Status"] = 2
        });

        ComAnulaveis entity = EntityMapper.MapRow<ComAnulaveis>(row, mappings);

        Assert.Equal(1234.56m, entity.Valor);
        Assert.Equal(7, entity.Quantidade);
        Assert.Equal(new DateTime(2026, 8, 5), entity.Data);
        Assert.Equal(StatusPedido.Fechado, entity.Status);
    }

    [Fact]
    public void CompiledSetter_LeavesAnAbsentColumnAtItsDefault()
    {
        FrozenDictionary<string, DaxColumnMapping> mappings =
            EntityMapper.GetColumnMappings(typeof(ComAnulaveis));
        var row = new DaxRow(new Dictionary<string, object?> { ["Valor"] = 1m });

        ComAnulaveis entity = EntityMapper.MapRow<ComAnulaveis>(row, mappings);

        Assert.Equal(1m, entity.Valor);
        Assert.Null(entity.Quantidade);
        Assert.Null(entity.Data);
        Assert.Null(entity.Status);
    }

    [Fact]
    public void CompiledSetter_DoesNotOverwriteWithANullCell()
    {
        FrozenDictionary<string, DaxColumnMapping> mappings =
            EntityMapper.GetColumnMappings(typeof(ComAnulaveis));
        var row = new DaxRow(new Dictionary<string, object?> { ["Valor"] = null });

        ComAnulaveis entity = EntityMapper.MapRow<ComAnulaveis>(row, mappings);

        Assert.Null(entity.Valor);
    }

    private sealed class ContaEscritas
    {
        public static int Escritas;

        private int _valor;

        public int Valor
        {
            get => _valor;
            set
            {
                _valor = value;
                Escritas++;
            }
        }
    }

    /// <summary>
    /// One write per matching cell — not per mapping key. A property with no attribute registers
    /// four keys (<c>Valor</c>, <c>[Valor]</c> and the two qualified forms), and the loop used to
    /// walk the keys: four lookups to find the single cell that exists.
    /// </summary>
    [Fact]
    public void EachMatchingCell_WritesTheProperty_Once()
    {
        ContaEscritas.Escritas = 0;

        FrozenDictionary<string, DaxColumnMapping> mappings =
            EntityMapper.GetColumnMappings(typeof(ContaEscritas));
        var row = new DaxRow(new Dictionary<string, object?> { ["Valor"] = 9 });

        ContaEscritas entity = EntityMapper.MapRow<ContaEscritas>(row, mappings);

        Assert.Equal(9, entity.Valor);
        Assert.Equal(1, ContaEscritas.Escritas);
    }

    [Fact]
    public void Materialization_IgnoresAColumnTheTypeDoesNotHave()
    {
        FrozenDictionary<string, DaxColumnMapping> mappings = EntityMapper.GetColumnMappings(typeof(SemAtributo));
        var row = new DaxRow(new Dictionary<string, object?>
        {
            ["Id"] = 3,
            ["ColunaQueNaoExisteNoTipo"] = "ignorada"
        });

        SemAtributo entity = EntityMapper.MapRow<SemAtributo>(row, mappings);

        Assert.Equal(3, entity.Id);
        Assert.Equal("", entity.Nome);
    }
}
