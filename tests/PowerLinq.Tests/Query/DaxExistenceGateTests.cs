using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Mapping;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Existence gate: <c>COUNTROWS</c> of another table as an extension column.
/// </summary>
/// <remarks>
/// <para>
/// <c>SUMMARIZECOLUMNS</c> discards the group when <b>all</b> the extension columns come back
/// BLANK. Counting the fact's rows is what prunes the dimension down to "only what has data" —
/// the behavior of a Power BI slicer. Pointing the <c>COUNTROWS</c> at the wrong table does not
/// return a wrong number: it returns the <b>entire</b> dimension, because pruning stops happening.
/// </para>
/// <para>
/// Measured against a real model: 15 rows with <c>COUNTROWS(FAT_EXPORTACAO_REGIME)</c> against 254
/// with <c>COUNTROWS(EMPRESA_RLS)</c> — and 254 is the dimension with no filter at all.
/// </para>
/// </remarks>
public sealed class DaxExistenceGateTests
{
    [DaxTable("EMPRESA_RLS")]
    private sealed class LinhaAncoradaNoRls
    {
        [DaxColumn("'EMPRESA_RLS'[GRUPO_EMPRESARIAL_ID]")]
        public string GrupoEmpresarialId { get; set; } = "";

        [DaxColumn("'DIM_BENEFICIARIO'[SK_BENEFICIARIO]")]
        public int BeneficiarioSk { get; set; }

        [DaxColumn("'DIM_BENEFICIARIO'[CNPJ]")]
        public string Cnpj { get; set; } = "";
    }

    [DaxTable("FAT_ISENCAO_REALIZADO")]
    private sealed class FatoRealizado
    {
        [DaxColumn("FAT_ISENCAO_REALIZADO[VL_REALIZADO]")]
        public decimal Valor { get; set; }
    }

    [DaxTable("FAT_ISENCAO_PREVISTO")]
    private sealed class FatoPrevisto
    {
        [DaxColumn("FAT_ISENCAO_PREVISTO[VL_PREVISTO]")]
        public decimal Valor { get; set; }
    }

    private sealed class Opcao
    {
        [DaxColumn("'DIM_BENEFICIARIO'[CNPJ]")]
        public string Cnpj { get; set; } = "";

        public long? RealizadoRows { get; set; }
    }

    private sealed class OpcaoComDuasPorteiras
    {
        [DaxColumn("'DIM_BENEFICIARIO'[CNPJ]")]
        public string Cnpj { get; set; } = "";

        public long? RealizadoRows { get; set; }
        public long? PrevistoRows { get; set; }
    }

    private sealed class NoopExecutor : IDaxQueryExecutor
    {
        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class => Task.FromResult(new List<T>());

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private static DaxTable<LinhaAncoradaNoRls> Table() => new(new NoopExecutor());

    // ---------- the gate points at the requested table ----------

    [Fact]
    public void CountOfAnotherTable_TargetsThatTable()
    {
        string dax = Table()
            .GroupBy(r => r.Cnpj)
            .Select(g => new Opcao { Cnpj = g.Key, RealizadoRows = g.Count<FatoRealizado>() })
            .ToDaxString();

        Assert.Contains("\"RealizadoRows\", COUNTROWS(FAT_ISENCAO_REALIZADO)", dax);

        // The bug: the gate pointing at the query's table switches the pruning off.
        Assert.DoesNotContain("COUNTROWS(EMPRESA_RLS)", dax);
    }

    [Fact]
    public void CountWithoutTypeArgument_StillCountsTheQueryTable()
    {
        // No regression: the argumentless overload still counts the query's table.
        string dax = Table()
            .GroupBy(r => r.Cnpj)
            .Select(g => new Opcao { Cnpj = g.Key, RealizadoRows = g.Count() })
            .ToDaxString();

        Assert.Contains("\"RealizadoRows\", COUNTROWS(EMPRESA_RLS)", dax);
    }

    [Fact]
    public void TwoGatesOfDifferentTables_EmitTwoDistinctCountRows()
    {
        // The real route uses two gates to get the UNION of the two facts: the group only drops
        // when both come back BLANK. Before, both came out identical, pointing at the query's table.
        string dax = Table()
            .GroupBy(r => r.Cnpj)
            .Select(g => new OpcaoComDuasPorteiras
            {
                Cnpj = g.Key,
                RealizadoRows = g.Count<FatoRealizado>(),
                PrevistoRows = g.Count<FatoPrevisto>()
            })
            .ToDaxString();

        Assert.Contains("\"RealizadoRows\", COUNTROWS(FAT_ISENCAO_REALIZADO)", dax);
        Assert.Contains("\"PrevistoRows\", COUNTROWS(FAT_ISENCAO_PREVISTO)", dax);
        Assert.NotEqual(
            dax.IndexOf("COUNTROWS(FAT_ISENCAO_REALIZADO)", StringComparison.Ordinal),
            dax.IndexOf("COUNTROWS(FAT_ISENCAO_PREVISTO)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheGateWorksAlongsideAForeignFilter()
    {
        // The two pieces that complete each other: the tenant filter and the gate. Without both,
        // there is no way to build an option list pruned by data.
        string dax = Table()
            .Where(r => r.GrupoEmpresarialId == "a4504464")
            .GroupBy(r => r.Cnpj)
            .Select(g => new Opcao { Cnpj = g.Key, RealizadoRows = g.Count<FatoRealizado>() })
            .ToDaxString();

        Assert.Contains("FILTER(", dax);
        Assert.Contains("'EMPRESA_RLS'[GRUPO_EMPRESARIAL_ID] = \"a4504464\"", dax);
        Assert.Contains("COUNTROWS(FAT_ISENCAO_REALIZADO)", dax);
    }

    [Fact]
    public void TheGateAlsoWorksWithoutGrouping()
    {
        string dax = Table()
            .Aggregate(g => new Opcao { RealizadoRows = g.Count<FatoRealizado>() })
            .ToDaxString();

        Assert.Contains("\"RealizadoRows\", COUNTROWS(FAT_ISENCAO_REALIZADO)", dax);
    }

    [Fact]
    public void ATableNameNeedingQuotes_IsQuotedInTheGate()
    {
        string dax = Table()
            .Aggregate(g => new Opcao { RealizadoRows = g.Count<FatoComNomeEspacado>() })
            .ToDaxString();

        Assert.Contains("COUNTROWS('Fato Com Espaco')", dax);
    }

    [DaxTable("Fato Com Espaco")]
    private sealed class FatoComNomeEspacado
    {
        public int Id { get; set; }
    }

    [Fact]
    public void AnUnattributedTypeUsesItsOwnName()
    {
        string dax = Table()
            .Aggregate(g => new Opcao { RealizadoRows = g.Count<SemAtributoDeTabela>() })
            .ToDaxString();

        Assert.Contains("COUNTROWS(SemAtributoDeTabela)", dax);
    }

    private sealed class SemAtributoDeTabela
    {
        public int Id { get; set; }
    }

    // ---------- BLANK does not become zero ----------

    /// <summary>
    /// What makes the pruning work is the BLANK, and that is why the gate returns <c>long?</c>.
    /// Materializing BLANK as <c>0</c> would hide the effect: the column would stop signaling
    /// "no data exists" and start asserting "zero rows exist", which is different.
    /// </summary>
    [Fact]
    public void ABlankGate_StaysNull_AndDoesNotBecomeZero()
    {
        var row = new DaxRow(new Dictionary<string, object?>
        {
            ["'DIM_BENEFICIARIO'[CNPJ]"] = "12345678000199",
            ["RealizadoRows"] = null
        });

        Opcao materializado = EntityMapper.MapRow<Opcao>(row, EntityMapper.GetColumnMappings(typeof(Opcao)));

        Assert.Equal("12345678000199", materializado.Cnpj);
        Assert.Null(materializado.RealizadoRows);
    }

    [Fact]
    public void APresentGate_Materializes()
    {
        var row = new DaxRow(new Dictionary<string, object?> { ["RealizadoRows"] = 15L });

        Opcao materializado = EntityMapper.MapRow<Opcao>(row, EntityMapper.GetColumnMappings(typeof(Opcao)));

        Assert.Equal(15L, materializado.RealizadoRows);
    }

    // ---------- the measured scenario ----------

    /// <summary>
    /// Reproduces the difference measured on the real model with an executor that returns the whole
    /// dimension when the gate points at the wrong table, and the subset when it points at the fact.
    /// The test does not talk to a server — it pins down the link between <b>which DAX is generated</b>
    /// and <b>how many rows that implies</b>, which is the part that was silently lost.
    /// </summary>
    [Fact]
    public async Task TheGateDecidesHowManyRowsComeBack()
    {
        var executor = new PruningExecutor();
        var table = new DaxTable<LinhaAncoradaNoRls>(executor);

        List<Opcao> comPorteiraCorreta = await table
            .GroupBy(r => r.Cnpj)
            .Select(g => new Opcao { Cnpj = g.Key, RealizadoRows = g.Count<FatoRealizado>() })
            .ToListAsync();

        List<Opcao> semPoda = await table
            .GroupBy(r => r.Cnpj)
            .Select(g => new Opcao { Cnpj = g.Key, RealizadoRows = g.Count() })
            .ToListAsync();

        Assert.Equal(15, comPorteiraCorreta.Count);
        Assert.Equal(254, semPoda.Count);
    }

    /// <summary>
    /// Executor that imitates the server's pruning: with the fact's <c>COUNTROWS</c> it returns 15
    /// rows, with the query table's <c>COUNTROWS</c> it returns the whole dimension's 254.
    /// </summary>
    private sealed class PruningExecutor : IDaxQueryExecutor
    {
        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class
        {
            int rows = daxQuery.Contains("COUNTROWS(FAT_ISENCAO_REALIZADO)", StringComparison.Ordinal)
                ? 15
                : 254;

            return Task.FromResult(Enumerable.Range(0, rows).Select(_ => Activator.CreateInstance<T>()).ToList());
        }

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
