using System.Collections.Frozen;
using System.Reflection;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Builders;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Mapping;
using PowerLinq.DaxConverter.Queries;
using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.Tests.Query;

/// <summary>
/// In DAX, a table name with a space or a special character requires single quotes:
/// <c>EVALUATE Ordem de Venda</c> is a syntax error. A name with a space is the norm in
/// Portuguese Power BI models, so the raw name used to break the query on the server.
/// </summary>
public sealed class DaxIdentifierQuotingTests
{
    [DaxTable("Ordem de Venda")]
    private sealed class OrdemVenda
    {
        public int Qtd { get; set; }
        public string Cliente { get; set; } = "";
    }

    [DaxTable("Produto")]
    private sealed class Produto
    {
        public int Id { get; set; }
    }

    [DaxTable("'Ja Aspada'")]
    private sealed class JaAspada
    {
        public int Id { get; set; }
    }

    [DaxTable("Cliente's Order")]
    private sealed class ComAspaInterna
    {
        public int Id { get; set; }
    }

    // Table name with a space and an explicit column: the attribute rules, and comes already quoted.
    [DaxTable("Fato Vendas")]
    private sealed class ComAtributoExplicito
    {
        [DaxColumn("'Fato Vendas'[Valor Total]")]
        public decimal ValorTotal { get; set; }
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

    private static DaxTable<T> Table<T>() where T : class => new(new NoopExecutor());

    // ---------- the quoting rule ----------

    [Theory]
    [InlineData("Produto", "Produto")]
    [InlineData("_Interno", "_Interno")]
    [InlineData("Tabela2", "Tabela2")]
    [InlineData("Ordem de Venda", "'Ordem de Venda'")]
    [InlineData("Fato-Vendas", "'Fato-Vendas'")]
    [InlineData("2024", "'2024'")]
    [InlineData("", "''")]
    [InlineData("Ação", "Ação")]
    public void Quote_AppliesOnlyWhenNeeded(string name, string expected) =>
        Assert.Equal(expected, DaxIdentifier.Quote(name));

    [Fact]
    public void Quote_DoublesAnEmbeddedSingleQuote() =>
        Assert.Equal("'Cliente''s Order'", DaxIdentifier.Quote("Cliente's Order"));

    [Fact]
    public void Quote_LeavesAnAlreadyQuotedNameAlone() =>
        Assert.Equal("'Ja Aspada'", DaxIdentifier.Quote("'Ja Aspada'"));

    [Fact]
    public void Column_EscapesClosingBracket() =>
        // In DAX, a ']' inside a column name is doubled.
        Assert.Equal("Produto[Nome]]Estranho]", DaxIdentifier.Column("Produto", "Nome]Estranho"));

    // ---------- generated DAX ----------

    /// <summary>
    /// A bare table: <c>DaxTable&lt;T&gt;</c> does not expose <c>ToDaxString</c>, so the DAX comes
    /// from the builder over an empty pipeline — the same path as <c>DaxPipelineBuilderTests</c>.
    /// </summary>
    private static string BareTableDax<T>() =>
        DaxPipelineBuilder.Build(new DaxPipeline(EntityMapper.GetTableName<T>(), typeof(T)));

    [Fact]
    public void SpacedTableName_IsQuotedInEvaluate() =>
        Assert.Equal("EVALUATE\n'Ordem de Venda'", BareTableDax<OrdemVenda>());

    [Fact]
    public void SpacedTableName_IsQuotedInFilterAndColumnReference()
    {
        string dax = Table<OrdemVenda>().Where(x => x.Qtd > 0).ToDaxString();

        Assert.Contains("FILTER(", dax);
        Assert.Contains("'Ordem de Venda'", dax);
        Assert.Contains("'Ordem de Venda'[Qtd] > 0", dax);

        // And never the raw form, which would be a syntax error.
        Assert.DoesNotContain("FILTER(\n    Ordem de Venda", dax.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void SpacedTableName_IsQuotedInOrderBy()
    {
        string dax = Table<OrdemVenda>().OrderBy(x => x.Cliente).ToDaxString();

        Assert.Contains("ORDER BY 'Ordem de Venda'[Cliente] ASC", dax);
    }

    [Fact]
    public async Task SpacedTableName_IsQuotedInCount()
    {
        var executor = new RecordingExecutor();
        var table = new DaxTable<OrdemVenda>(executor);

        await table.Where(x => x.Qtd > 0).CountAsync();

        Assert.Contains(
            "COUNTROWS(FILTER('Ordem de Venda', 'Ordem de Venda'[Qtd] > 0))",
            executor.LastQuery);
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

    [Fact]
    public void SimpleTableName_StaysUnquoted()
    {
        string dax = Table<Produto>().Where(x => x.Id > 1).ToDaxString();

        Assert.Contains("FILTER(", dax);
        Assert.Contains("Produto[Id] > 1", dax);
        Assert.DoesNotContain("'Produto'", dax);
    }

    [Fact]
    public void AlreadyQuotedTableName_IsNotQuotedTwice()
    {
        string dax = BareTableDax<JaAspada>();

        Assert.Equal("EVALUATE\n'Ja Aspada'", dax);
        Assert.DoesNotContain("''Ja Aspada''", dax);
    }

    [Fact]
    public void TableNameWithSingleQuote_IsEscaped() =>
        Assert.Equal("EVALUATE\n'Cliente''s Order'", BareTableDax<ComAspaInterna>());

    [Fact]
    public void ExplicitColumnAttribute_IsUsedVerbatim()
    {
        string dax = Table<ComAtributoExplicito>().Where(x => x.ValorTotal > 0m).ToDaxString();

        Assert.Contains("'Fato Vendas'[Valor Total] > 0", dax);
    }

    [Fact]
    public void SpacedTableName_IsQuotedInGroupBy()
    {
        // BuildGroupBy builds the DaxTableRef from the name, so it goes through the same quoting.
        string dax = Table<OrdemVenda>()
            .Where(x => x.Qtd > 0)
            .GroupBy(x => x.Cliente)
            .Select(g => new Agrupado { Cliente = g.Key, Total = g.Count() })
            .ToDaxString();

        Assert.Contains("'Ordem de Venda'[Cliente]", dax);
        Assert.Contains("FILTER('Ordem de Venda',", dax);
    }

    [Fact]
    public void SpacedTableName_IsQuotedInSelect()
    {
        string dax = Table<OrdemVenda>()
            .Select(x => new Projetado { Cliente = x.Cliente })
            .ToDaxString();

        Assert.Contains("SELECTCOLUMNS('Ordem de Venda'", dax);
        Assert.Contains("'Ordem de Venda'[Cliente]", dax);
    }

    private sealed class Agrupado
    {
        [DaxColumn("'Ordem de Venda'[Cliente]")] public string Cliente { get; set; } = "";
        [DaxColumn("[Total]")] public long Total { get; set; }
    }

    private sealed class Projetado
    {
        public string Cliente { get; set; } = "";
    }

    // ---------- the real risk: generation and materialization have to agree ----------

    [Fact]
    public void Mapping_AcceptsBothTheQuotedAndUnquotedColumnName()
    {
        // The query asks for the quoted table, but the server usually returns the column name
        // unquoted in the metadata. If the mapping knew only one of the shapes, the query would
        // work and materialization would return the default, silently.
        FrozenDictionary<string, DaxColumnMapping> mappings = EntityMapper.GetColumnMappings(typeof(OrdemVenda));

        Assert.True(mappings.ContainsKey("'Ordem de Venda'[Qtd]"), "forma aspada ausente");
        Assert.True(mappings.ContainsKey("Ordem de Venda[Qtd]"), "forma sem aspas ausente");
    }

    [Theory]
    [InlineData("'Ordem de Venda'[Qtd]")]
    [InlineData("Ordem de Venda[Qtd]")]
    [InlineData("[Qtd]")]
    [InlineData("Qtd")]
    public void Materialization_WorksForEveryColumnNameForm(string columnName)
    {
        var row = new DaxRow(new Dictionary<string, object?> { [columnName] = 42 });

        OrdemVenda entity = EntityMapper.MapRow<OrdemVenda>(
            row,
            EntityMapper.GetColumnMappings(typeof(OrdemVenda)));

        Assert.Equal(42, entity.Qtd);
    }

    [Fact]
    public void GeneratedReferenceAndMappingKey_AreTheSameString()
    {
        // The invariant that prevents the silent bug: what the translator emits has to exist as a
        // key in the mapping.
        PropertyInfo property = typeof(OrdemVenda).GetProperty(nameof(OrdemVenda.Qtd))!;
        string generated = DaxIdentifier.Column("Ordem de Venda", property.Name);

        FrozenDictionary<string, DaxColumnMapping> mappings = EntityMapper.GetColumnMappings(typeof(OrdemVenda));

        Assert.True(
            mappings.ContainsKey(generated),
            $"o tradutor emite '{generated}', que o mapeamento não conhece");
    }

    // ---------- the reference's owner table ----------

    /// <summary>
    /// <c>TableOf</c> decides the filter's shape: getting the owner table wrong generates DAX the
    /// server refuses, or worse, filters the wrong table. That is why the parse is tested across the
    /// quoting shapes the attribute accepts, and not only the simple one.
    /// </summary>
    [Theory]
    [InlineData("Produto[Nome]", "Produto")]
    [InlineData("'Ordem de Venda'[Qtd]", "Ordem de Venda")]
    [InlineData("'Rock ''n'' Roll'[Faixa]", "Rock 'n' Roll")]
    [InlineData("Produto[Nome]]Interno]", "Produto")]
    [InlineData("'DIM_EMPRESA'[CNPJ_EMPRESA]", "DIM_EMPRESA")]
    public void TableOf_ReadsTheTablePart(string reference, string expected) =>
        Assert.Equal(expected, DaxIdentifier.TableOf(reference));

    /// <summary>
    /// With no table named, it returns <c>null</c> — and the caller treats it as a column of the
    /// current context, the behavior that predates this function. A malformed reference takes the
    /// same path: throwing here would trade suspicious DAX for a failure in a query that worked.
    /// </summary>
    [Theory]
    [InlineData("[Total]")]              // coluna de extensão
    [InlineData("Categoria")]            // nome isolado
    [InlineData("")]
    [InlineData("'")]
    [InlineData("'Sem fechamento[X]")]   // aspa nunca fechada
    [InlineData("'A'X[B]")]              // lixo entre o nome e o colchete
    public void TableOf_WithoutATableName_IsNull(string reference) =>
        Assert.Null(DaxIdentifier.TableOf(reference));

    [Theory]
    [InlineData("FAT", "fat")]
    [InlineData("'FAT'", "FAT")]
    [InlineData("'Ordem de Venda'", "ordem de venda")]
    public void SameTable_IgnoresQuotingAndCase(string left, string right) =>
        Assert.True(DaxIdentifier.SameTable(left, right));

    [Fact]
    public void SameTable_DistinguishesDifferentNames() =>
        Assert.False(DaxIdentifier.SameTable("DIM_EMPRESA", "EMPRESA_RLS"));
}
