using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// Filtering over a column of another table of the model.
/// </summary>
/// <remarks>
/// <para>
/// <c>FILTER(table, predicate)</c> opens a <b>row context</b> on the iterated table. In that
/// context, a reference to another table's column — related or not — is a <b>DAX error</b>, not a
/// different result: <i>"a single value for column X in table Y cannot be determined"</i>. Row
/// context and filter context are not interchangeable, and that is why the filter's shape depends
/// on the table that owns the column.
/// </para>
/// <para>
/// The model in these tests reproduces the real topology where the problem was observed: a fact, a
/// dimension and an RLS table, with the foreign columns mapped by qualified reference.
/// </para>
/// </remarks>
public sealed class DaxRelatedTableFilterTests
{
    [DaxTable("FAT_EXPORTACAO_REGIME")]
    private sealed class Exportacao
    {
        [DaxColumn("FAT_EXPORTACAO_REGIME[VL_REALIZADO_BRL]")]
        public decimal Realizado { get; set; }

        [DaxColumn("FAT_EXPORTACAO_REGIME[ANO]")]
        public int Ano { get; set; }

        [DaxColumn("'EMPRESA_RLS'[GRUPO_EMPRESARIAL_ID]")]
        public string GrupoEmpresarialId { get; set; } = "";

        [DaxColumn("'DIM_EMPRESA'[CNPJ_EMPRESA]")]
        public string Cnpj { get; set; } = "";

        [DaxColumn("'DIM_EMPRESA'[RAZAO_SOCIAL]")]
        public string RazaoSocial { get; set; } = "";
    }

    private sealed class PorEmpresa
    {
        [DaxColumn("'DIM_EMPRESA'[CNPJ_EMPRESA]")]
        public string Cnpj { get; set; } = "";

        public decimal Realizado { get; set; }
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

    private static DaxTable<Exportacao> Table() => new(new NoopExecutor());

    // ---------- a column from another table ----------

    [Fact]
    public void Where_OnARlsColumn_WrapsTheSourceInCalculateTable()
    {
        string dax = Table()
            .Where(e => e.GrupoEmpresarialId == "a4504464")
            .ToDaxString();

        Assert.Contains("CALCULATETABLE(", dax);
        Assert.Contains("FILTER(", dax);
        Assert.Contains("EMPRESA_RLS", dax);
        Assert.Contains("'EMPRESA_RLS'[GRUPO_EMPRESARIAL_ID] = \"a4504464\"", dax);

        // What characterizes the bug: the other table's predicate inside a FILTER over the fact.
        Assert.DoesNotContain(
            "FILTER(\n    FAT_EXPORTACAO_REGIME,\n    'EMPRESA_RLS'", dax.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// The exact shape, compared against the one validated against a real model through ADOMD. It
    /// stays a literal assertion on purpose: what breaks here is the generated DAX's format, and
    /// that is what determines whether the server accepts the query.
    /// </summary>
    [Fact]
    public void Where_OnARlsColumn_GeneratesTheFormValidatedAgainstTheServer()
    {
        string dax = Table()
            .Where(e => e.GrupoEmpresarialId == "a4504464")
            .ToDaxString()
            .Replace("\r\n", "\n");

        Assert.Equal(
            """
            EVALUATE
            CALCULATETABLE(
                FAT_EXPORTACAO_REGIME,
                FILTER(
                    EMPRESA_RLS,
                    'EMPRESA_RLS'[GRUPO_EMPRESARIAL_ID] = "a4504464"
                )
            )
            """.Replace("\r\n", "\n"),
            dax);
    }

    [Fact]
    public void Where_OnADimensionColumn_FiltersTheDimensionNotTheFact()
    {
        string dax = Table()
            .Where(e => e.Cnpj == "12345678000199")
            .ToDaxString();

        // The FILTER has to iterate DIM_EMPRESA, which owns the column.
        Assert.Contains("FILTER(", dax);
        Assert.Contains("DIM_EMPRESA,", Squash(dax));
        Assert.Contains("'DIM_EMPRESA'[CNPJ_EMPRESA] = \"12345678000199\"", dax);
    }

    // ---------- no regression: a column of the fact itself ----------

    [Fact]
    public void Where_OnlyOnOwnColumns_KeepsTheCurrentFilterForm()
    {
        string dax = Table()
            .Where(e => e.Realizado > 0)
            .ToDaxString();

        Assert.Contains("FILTER(FAT_EXPORTACAO_REGIME, FAT_EXPORTACAO_REGIME[VL_REALIZADO_BRL] > 0)", Squash(dax));
        Assert.DoesNotContain("CALCULATETABLE", dax);
    }

    [Fact]
    public void Where_TwoOwnColumns_StaysASingleFilter()
    {
        string dax = Squash(Table()
            .Where(e => e.Realizado > 0 && e.Ano == 2026)
            .ToDaxString());

        Assert.DoesNotContain("CALCULATETABLE", dax);
        Assert.Equal(1, Occurrences(dax, "FILTER("));
        Assert.Contains("FAT_EXPORTACAO_REGIME[VL_REALIZADO_BRL] > 0 && FAT_EXPORTACAO_REGIME[ANO] = 2026", dax);
    }

    // ---------- mixed predicate ----------

    [Fact]
    public void Where_MixingFactAndDimension_SplitsIntoOneFilterPerTable()
    {
        string dax = Squash(Table()
            .Where(e => e.Realizado > 0 && e.Cnpj == "12345678000199")
            .ToDaxString());

        Assert.Contains("CALCULATETABLE(", dax);
        Assert.Contains("FILTER(FAT_EXPORTACAO_REGIME, FAT_EXPORTACAO_REGIME[VL_REALIZADO_BRL] > 0)", dax);
        Assert.Contains("FILTER(DIM_EMPRESA, 'DIM_EMPRESA'[CNPJ_EMPRESA] = \"12345678000199\")", dax);
    }

    [Fact]
    public void Where_TouchingTwoForeignTables_EmitsOneFilterEach()
    {
        string dax = Squash(Table()
            .Where(e => e.GrupoEmpresarialId == "g" && e.Cnpj == "c")
            .ToDaxString());

        Assert.Contains("FILTER(EMPRESA_RLS, 'EMPRESA_RLS'[GRUPO_EMPRESARIAL_ID] = \"g\")", dax);
        Assert.Contains("FILTER(DIM_EMPRESA, 'DIM_EMPRESA'[CNPJ_EMPRESA] = \"c\")", dax);
        Assert.Equal(2, Occurrences(dax, "FILTER("));
    }

    [Fact]
    public void ChainedWhere_OnTheSameForeignTable_CombinesIntoOneFilter()
    {
        string dax = Squash(Table()
            .Where(e => e.Cnpj == "c")
            .Where(e => e.RazaoSocial == "ACME")
            .ToDaxString());

        Assert.Equal(1, Occurrences(dax, "FILTER("));
        Assert.Contains(
            "FILTER(DIM_EMPRESA, 'DIM_EMPRESA'[CNPJ_EMPRESA] = \"c\" && 'DIM_EMPRESA'[RAZAO_SOCIAL] = \"ACME\")",
            dax);
    }

    // ---------- the grouped path ----------

    [Fact]
    public void GroupBy_WithAForeignFilter_PassesItAsAFilterArgument()
    {
        string dax = Squash(Table()
            .Where(e => e.GrupoEmpresarialId == "g")
            .GroupBy(e => e.Cnpj)
            .Select(g => new PorEmpresa { Cnpj = g.Key, Realizado = g.Sum(e => e.Realizado) })
            .ToDaxString());

        Assert.Contains("SUMMARIZECOLUMNS(", dax);
        Assert.Contains("FILTER(EMPRESA_RLS, 'EMPRESA_RLS'[GRUPO_EMPRESARIAL_ID] = \"g\")", dax);
        Assert.Contains("\"Realizado\", SUM(FAT_EXPORTACAO_REGIME[VL_REALIZADO_BRL])", dax);

        // A SUMMARIZECOLUMNS filter table argument already is filter context: it needs no
        // CALCULATETABLE around it.
        Assert.DoesNotContain("CALCULATETABLE", dax);
    }

    [Fact]
    public void Aggregate_WithAForeignFilter_PassesItAsAFilterArgument()
    {
        string dax = Squash(Table()
            .Where(e => e.Cnpj == "c")
            .Aggregate(g => new PorEmpresa { Realizado = g.Sum(e => e.Realizado) })
            .ToDaxString());

        Assert.Contains("FILTER(DIM_EMPRESA, 'DIM_EMPRESA'[CNPJ_EMPRESA] = \"c\")", dax);
    }

    // ---------- counting ----------

    [Fact]
    public async Task CountAsync_WithAForeignFilter_CountsInsideCalculateTable()
    {
        var recorder = new RecordingExecutor();
        var table = new DaxTable<Exportacao>(recorder);

        await table.Where(e => e.Cnpj == "c").CountAsync();

        string dax = Squash(recorder.LastQuery!);
        Assert.Contains("COUNTROWS(CALCULATETABLE(", dax);
        Assert.Contains("FILTER(DIM_EMPRESA, 'DIM_EMPRESA'[CNPJ_EMPRESA] = \"c\")", dax);
    }

    private sealed class RecordingExecutor : IDaxQueryExecutor
    {
        public string? LastQuery { get; private set; }

        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class
        {
            LastQuery = daxQuery;
            return Task.FromResult(new List<T>());
        }

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            return Task.FromResult<object?>(null);
        }

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default)
        {
            LastQuery = daxQuery;
            return Task.FromResult(0);
        }
    }

    // ---------- a window after the filter ----------

    [Fact]
    public void Take_AfterAForeignFilter_WindowsTheFilteredTable()
    {
        string dax = Squash(Table()
            .Where(e => e.Cnpj == "c")
            .OrderBy(e => e.Ano)
            .Take(5)
            .ToDaxString());

        // The TOPN has to sit on the outside: filter, then limit.
        int topN = dax.IndexOf("TOPN(", StringComparison.Ordinal);
        int calculate = dax.IndexOf("CALCULATETABLE(", StringComparison.Ordinal);

        Assert.True(topN >= 0 && calculate > topN, $"TOPN deve envolver o CALCULATETABLE. DAX: {dax}");
    }

    // ---------- not separable ----------

    [Fact]
    public void Where_ComparingColumnsOfDifferentTables_IsRefusedWithAClearMessage()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
            Table().Where(e => e.Cnpj == e.GrupoEmpresarialId).ToDaxString());

        Assert.Contains("more than one table", ex.Message);
        Assert.Contains("DIM_EMPRESA", ex.Message);
        Assert.Contains("EMPRESA_RLS", ex.Message);
    }

    [Fact]
    public void Where_OrAcrossTables_IsRefusedBecauseItCannotBeSplit()
    {
        // && splits; || does not. One filter per table is an intersection, so there is no way to
        // represent the union across tables as filter arguments.
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
            Table().Where(e => e.Realizado > 0 || e.Cnpj == "c").ToDaxString());

        Assert.Contains("more than one table", ex.Message);
    }

    [Fact]
    public void MultipleTablesMessage_IsLocalized()
    {
        IPowerLinqLocalizer portuguese = new ResourceManagerPowerLinqLocalizer("pt-BR");

        Assert.Contains(
            "mais de uma tabela",
            portuguese.Format("FilterSpansMultipleTables", "A, B"));
    }

    /// <summary>Collapses the writer's formatting, so the assertions talk about shape and not layout.</summary>
    private static string Squash(string dax)
    {
        string[] parts = dax.Split((char[])['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts).Replace("( ", "(").Replace(" )", ")");
    }

    private static int Occurrences(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
