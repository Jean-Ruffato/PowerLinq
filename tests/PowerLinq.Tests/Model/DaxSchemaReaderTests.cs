using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Model;

namespace PowerLinq.Tests.Model;

/// <summary>
/// Reading the model's metadata through the Analysis Services schema tables.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is verified here is the mapping</b>, with an executor that returns rows shaped like
/// <c>TMSCHEMA</c>'s. The DMV's <b>column names and codes</b> come from the TMSCHEMA documentation
/// and were <b>not</b> probed against a real endpoint in this work — anyone with a model at hand
/// should check before trusting the first read.
/// </para>
/// <para>
/// What these tests pin down is what a test can pin down with no server: the link by ID among the
/// four queries, the discarding of the row-number column, the cardinality resolution, and the
/// discarding of a relationship whose side does not resolve.
/// </para>
/// </remarks>
public sealed class DaxSchemaReaderTests
{
    /// <summary>
    /// Executor that answers each DMV query by what it mentions.
    /// </summary>
    /// <remarks>
    /// It matches by the schema table's name, and not by call order: pinning the order would make
    /// the test fail on a refactor that merely swapped two reads, pointing at the wrong
    /// place.
    /// </remarks>
    private sealed class DmvExecutor(Dictionary<string, List<Dictionary<string, object?>>> tables)
        : IDaxRawQueryExecutor
    {
        public List<string> Queries { get; } = [];

        public Task<DaxResult> ExecuteRowsAsync(
            string daxQuery,
            CancellationToken cancellationToken = default)
        {
            Queries.Add(daxQuery);

            foreach ((string name, List<Dictionary<string, object?>> rows) in tables)
            {
                if (!daxQuery.Contains(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                return Task.FromResult(new DaxResult(
                    rows.Count == 0 ? [] : [.. rows[0].Keys],
                    [.. rows.Select(row => new DaxRow(row))]));
            }

            return Task.FromResult(DaxResult.Empty());
        }
    }

    private static DmvExecutor Executor() => new(new()
    {
        ["TMSCHEMA_TABLES"] =
        [
            new() { ["ID"] = 1L, ["Name"] = "FAT_ISENCAO" },
            new() { ["ID"] = 2L, ["Name"] = "DIM_EMPRESA" }
        ],
        ["TMSCHEMA_COLUMNS"] =
        [
            new()
            {
                ["ID"] = 10L, ["TableID"] = 1L, ["ExplicitName"] = "PERIODO_FECHAMENTO",
                ["InferredName"] = null, ["ExplicitDataType"] = 2, ["Type"] = 1, ["IsHidden"] = false
            },
            new()
            {
                ["ID"] = 11L, ["TableID"] = 1L, ["ExplicitName"] = "EMPRESA_ID",
                ["InferredName"] = null, ["ExplicitDataType"] = 6, ["Type"] = 1, ["IsHidden"] = false
            },
            // Row-number column: Type 3, internal to the engine.
            new()
            {
                ["ID"] = 12L, ["TableID"] = 1L, ["ExplicitName"] = null,
                ["InferredName"] = "RowNumber-2662979B", ["ExplicitDataType"] = 6, ["Type"] = 3,
                ["IsHidden"] = true
            },
            // Calculated column with no explicit name: the inferred one is what counts.
            new()
            {
                ["ID"] = 13L, ["TableID"] = 2L, ["ExplicitName"] = null,
                ["InferredName"] = "ID", ["ExplicitDataType"] = 6, ["Type"] = 2, ["IsHidden"] = false
            }
        ],
        ["TMSCHEMA_MEASURES"] =
        [
            new()
            {
                ["Name"] = "Total Vendas", ["TableID"] = 1L, ["DataType"] = 10, ["IsHidden"] = false
            }
        ],
        ["TMSCHEMA_RELATIONSHIPS"] =
        [
            new()
            {
                ["FromTableID"] = 1L, ["FromColumnID"] = 11L, ["FromCardinality"] = 2,
                ["ToTableID"] = 2L, ["ToColumnID"] = 13L, ["ToCardinality"] = 1,
                ["IsActive"] = true
            },
            // A side that does not resolve: column 99 does not exist.
            new()
            {
                ["FromTableID"] = 1L, ["FromColumnID"] = 99L, ["FromCardinality"] = 2,
                ["ToTableID"] = 2L, ["ToColumnID"] = 13L, ["ToCardinality"] = 1,
                ["IsActive"] = true
            }
        ]
    });

    /// <summary>
    /// The TMSCHEMA type codes become names, and a code outside the table becomes <c>Unknown</c>
    /// instead of bringing the read down.
    /// </summary>
    [Theory]
    [InlineData(2, "String")]
    [InlineData(6, "Int64")]
    [InlineData(8, "Double")]
    [InlineData(9, "DateTime")]
    [InlineData(10, "Decimal")]
    [InlineData(11, "Boolean")]
    [InlineData(17, "Binary")]
    [InlineData(19, "Variant")]
    [InlineData(999, "Unknown")]
    public async Task ColumnDataTypeCodes_MapToNames(int code, string expected)
    {
        var executor = new DmvExecutor(new()
        {
            ["TMSCHEMA_TABLES"] = [new() { ["ID"] = 1L, ["Name"] = "T" }],
            ["TMSCHEMA_COLUMNS"] =
            [
                new()
                {
                    ["ID"] = 10L, ["TableID"] = 1L, ["ExplicitName"] = "C",
                    ["InferredName"] = null, ["ExplicitDataType"] = code, ["Type"] = 1,
                    ["IsHidden"] = false
                }
            ]
        });

        DaxModelSchema schema = await new DaxSchemaReader(executor).ReadAsync();

        DaxSchemaColumn column = Assert.Single(schema.Table("T")!.Columns);
        Assert.Equal(expected, column.DataType);
    }

    /// <summary>
    /// ADOMD returns the TMSCHEMA booleans as <see cref="bool"/>, but not every provider agrees:
    /// a numeric <c>1</c>/<c>0</c> is read the same, and a missing column counts as false.
    /// </summary>
    [Theory]
    [InlineData(1L, true)]
    [InlineData(0L, false)]
    [InlineData("ausente", false)]
    public async Task Flags_AcceptBoolNumericAndAbsent(object hiddenMarker, bool expectedHidden)
    {
        var column = new Dictionary<string, object?>
        {
            ["ID"] = 10L, ["TableID"] = 1L, ["ExplicitName"] = "C",
            ["InferredName"] = null, ["ExplicitDataType"] = 6, ["Type"] = 1
        };

        // "missing" is the signal not to include the IsHidden column at all.
        if (hiddenMarker is not "ausente")
            column["IsHidden"] = hiddenMarker;

        var executor = new DmvExecutor(new()
        {
            ["TMSCHEMA_TABLES"] = [new() { ["ID"] = 1L, ["Name"] = "T" }],
            ["TMSCHEMA_COLUMNS"] = [column]
        });

        DaxModelSchema schema = await new DaxSchemaReader(executor).ReadAsync();

        Assert.Equal(expectedHidden, Assert.Single(schema.Table("T")!.Columns).IsHidden);
    }

    [Fact]
    public async Task TablesAndColumns_AreLinkedByIdNotByName()
    {
        DaxModelSchema schema = await new DaxSchemaReader(Executor()).ReadAsync();

        Assert.Equal(2, schema.Tables.Count);
        Assert.True(schema.HasColumn("FAT_ISENCAO", "PERIODO_FECHAMENTO"));
        Assert.True(schema.HasColumn("DIM_EMPRESA", "ID"));
        Assert.False(schema.HasColumn("DIM_EMPRESA", "PERIODO_FECHAMENTO"));
    }

    /// <summary>
    /// The row-number column is discarded: it is internal to the engine, appears in no client, and
    /// a contract generated with it would have a property nobody can use.
    /// </summary>
    [Fact]
    public async Task TheRowNumberColumn_IsDropped()
    {
        DaxModelSchema schema = await new DaxSchemaReader(Executor()).ReadAsync();

        Assert.DoesNotContain(
            schema.Table("FAT_ISENCAO")!.Columns,
            column => column.Name.StartsWith("RowNumber", StringComparison.Ordinal));
    }

    /// <summary>With no explicit name, the inferred one counts — the calculated column's case.</summary>
    [Fact]
    public async Task AColumnWithoutAnExplicitName_UsesTheInferredOne()
    {
        DaxModelSchema schema = await new DaxSchemaReader(Executor()).ReadAsync();

        Assert.Contains(
            schema.Table("DIM_EMPRESA")!.Columns,
            column => column.Name == "ID");
    }

    [Fact]
    public async Task Measures_CarryTheTableTheyBelongTo()
    {
        DaxModelSchema schema = await new DaxSchemaReader(Executor()).ReadAsync();

        DaxSchemaMeasure measure = Assert.Single(schema.Measures);

        Assert.Equal("Total Vendas", measure.Name);
        Assert.Equal("FAT_ISENCAO", measure.Table);
        Assert.Equal("Decimal", measure.DataType);
    }

    /// <summary>
    /// The cardinality comes from the TMSCHEMA codes, and the fact sits on the <b>many</b> side —
    /// which is what the navigation check will consult.
    /// </summary>
    [Fact]
    public async Task Cardinality_ComesFromTheSchemaCodes()
    {
        DaxModelSchema schema = await new DaxSchemaReader(Executor()).ReadAsync();

        DaxSchemaRelationship relationship = Assert.Single(schema.Relationships);

        Assert.Equal("FAT_ISENCAO", relationship.FromTable);
        Assert.Equal(DaxCardinality.Many, relationship.FromCardinality);
        Assert.Equal(DaxCardinality.One, relationship.ToCardinality);
        Assert.True(relationship.IsActive);
    }

    /// <summary>
    /// A relationship whose side does not resolve is <b>discarded</b>, not kept with an empty table:
    /// a path to nowhere would make the ambiguity detection count a path that does not exist — and
    /// an invented ambiguity is worse than no detection at all.
    /// </summary>
    [Fact]
    public async Task ARelationshipWithAnUnresolvableSide_IsDropped()
    {
        DaxModelSchema schema = await new DaxSchemaReader(Executor()).ReadAsync();

        Assert.Single(schema.Relationships);
        Assert.All(schema.Relationships, r => Assert.NotEqual("", r.FromColumn));
    }

    /// <summary>
    /// The four queries are DMV, not DAX: they do not start at <c>EVALUATE</c>, and that is why the
    /// read goes through the raw path instead of the door that validates the text.
    /// </summary>
    [Fact]
    public async Task TheQueriesAreDmv_NotDax()
    {
        var executor = Executor();

        await new DaxSchemaReader(executor).ReadAsync();

        Assert.Equal(4, executor.Queries.Count);
        Assert.All(executor.Queries, query =>
        {
            Assert.StartsWith("SELECT ", query, StringComparison.Ordinal);
            Assert.Contains("$SYSTEM.TMSCHEMA_", query, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A model that returns nothing is not an error: an empty schema is the correct result, and the
    /// consumer reports divergence on everything — which is the right information.
    /// </summary>
    [Fact]
    public async Task AnEmptyModel_ReadsAsAnEmptySchema()
    {
        DaxModelSchema schema = await new DaxSchemaReader(new DmvExecutor([])).ReadAsync();

        Assert.Empty(schema.Tables);
        Assert.Empty(schema.Measures);
        Assert.Empty(schema.Relationships);
    }
}
