using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;

namespace PowerLinq.DaxConverter.Model;

/// <summary>
/// Reads the semantic model's metadata through the Analysis Services schema tables.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the only point of this feature that touches the network</b>, and it is called
/// explicitly — not on the query composition path. That is what preserves the central property:
/// <c>ToDaxString()</c> stays synchronous and still cannot fail because the endpoint is
/// unavailable. Whoever generates a contract or validates one works over the
/// <see cref="DaxModelSchema"/> that was already read, typically loaded from a file.
/// </para>
/// <para>
/// It requires <see cref="IDaxRawQueryExecutor"/> rather than <see cref="IDaxQueryExecutor"/>: a
/// DMV query does not return rows of a contract, it returns columns only this reader knows. The
/// raw path already existed for pagination, so no executor implementation gains a method because
/// of this.
/// </para>
/// <para>
/// <b>The queries are DMV, not DAX.</b> They travel over the same endpoint, but the syntax is
/// <c>SELECT ... FROM $SYSTEM...</c> — which is why they do not go through <c>DaxContext</c>'s
/// "starts with EVALUATE or DEFINE" validation, which is about the escape hatch and not about
/// this.
/// </para>
/// </remarks>
public sealed class DaxSchemaReader(IDaxRawQueryExecutor executor)
{
    private readonly IDaxRawQueryExecutor _executor =
        executor ?? throw new ArgumentNullException(nameof(executor));

    /// <summary>Reads the whole schema: tables, columns, measures and relationships.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The four queries are sequential, not parallel: a pooled executor serializes per connection
    /// anyway, and firing four in parallel would take four connections from the pool to save time
    /// that is not the bottleneck — this runs once, off the execution path.
    /// </remarks>
    public async Task<DaxModelSchema> ReadAsync(CancellationToken cancellationToken = default)
    {
        // The link between the four queries is by ID, not by name: that is how TMSCHEMA relates
        // them, and resolving by name would require names to be unique — which they are, but
        // depending on that would break on a model with a table renamed during the read.
        Dictionary<long, string> tableNames = await ReadTableNamesAsync(cancellationToken);
        Dictionary<long, string> columnNames = [];

        var columnsByTable = new Dictionary<long, List<DaxSchemaColumn>>();

        foreach (DaxRow row in await RowsAsync(ColumnsQuery, cancellationToken))
        {
            long tableId = row.GetLong("TableID");
            string name = ColumnName(row);

            // The row-number column is internal to the engine — it appears in no client and
            // referencing it is not DAX anyone writes. Type 3 is RowNumber in TMSCHEMA.
            if (row.GetInt("Type") == RowNumberColumnType || string.IsNullOrEmpty(name))
                continue;

            columnNames[row.GetLong("ID")] = name;

            if (!columnsByTable.TryGetValue(tableId, out List<DaxSchemaColumn>? columns))
                columnsByTable[tableId] = columns = [];

            columns.Add(new DaxSchemaColumn(name, DataTypeName(row.GetInt("ExplicitDataType")), Flag(row, "IsHidden")));
        }

        var tables = new List<DaxSchemaTable>();

        foreach ((long id, string name) in tableNames)
        {
            tables.Add(new DaxSchemaTable(
                name,
                columnsByTable.GetValueOrDefault(id, []),
                IsHidden: false));
        }

        return new DaxModelSchema(
            tables,
            await ReadMeasuresAsync(tableNames, cancellationToken),
            await ReadRelationshipsAsync(tableNames, columnNames, cancellationToken));
    }

    private async Task<Dictionary<long, string>> ReadTableNamesAsync(CancellationToken cancellationToken)
    {
        var names = new Dictionary<long, string>();

        foreach (DaxRow row in await RowsAsync(TablesQuery, cancellationToken))
        {
            if (row.GetString("Name") is { Length: > 0 } name)
                names[row.GetLong("ID")] = name;
        }

        return names;
    }

    private async Task<List<DaxSchemaMeasure>> ReadMeasuresAsync(
        Dictionary<long, string> tableNames,
        CancellationToken cancellationToken)
    {
        var measures = new List<DaxSchemaMeasure>();

        foreach (DaxRow row in await RowsAsync(MeasuresQuery, cancellationToken))
        {
            if (row.GetString("Name") is not { Length: > 0 } name)
                continue;

            measures.Add(new DaxSchemaMeasure(
                name,
                tableNames.GetValueOrDefault(row.GetLong("TableID"), ""),
                DataTypeName(row.GetInt("DataType")),
                Flag(row, "IsHidden")));
        }

        return measures;
    }

    private async Task<List<DaxSchemaRelationship>> ReadRelationshipsAsync(
        Dictionary<long, string> tableNames,
        Dictionary<long, string> columnNames,
        CancellationToken cancellationToken)
    {
        var relationships = new List<DaxSchemaRelationship>();

        foreach (DaxRow row in await RowsAsync(RelationshipsQuery, cancellationToken))
        {
            // A relationship whose side does not resolve is dropped, rather than reported as a
            // relationship with an empty table: it would point at a table or column the read
            // filtered out (a row-number column, for example), and a path to nowhere would make
            // ambiguity detection count a path that does not exist.
            if (tableNames.GetValueOrDefault(row.GetLong("FromTableID")) is not { } fromTable
                || tableNames.GetValueOrDefault(row.GetLong("ToTableID")) is not { } toTable
                || columnNames.GetValueOrDefault(row.GetLong("FromColumnID")) is not { } fromColumn
                || columnNames.GetValueOrDefault(row.GetLong("ToColumnID")) is not { } toColumn)
            {
                continue;
            }

            relationships.Add(new DaxSchemaRelationship(
                fromTable,
                fromColumn,
                Cardinality(row.GetInt("FromCardinality")),
                toTable,
                toColumn,
                Cardinality(row.GetInt("ToCardinality")),
                Flag(row, "IsActive")));
        }

        return relationships;
    }

    private async Task<IReadOnlyList<DaxRow>> RowsAsync(string query, CancellationToken cancellationToken)
    {
        DaxResult result = await _executor.ExecuteRowsAsync(query, cancellationToken);

        return result.Rows;
    }

    /// <summary>
    /// The column's name: the explicit one, or the inferred one when the engine created it.
    /// </summary>
    private static string ColumnName(DaxRow row) =>
        row.GetString("ExplicitName") is { Length: > 0 } explicitName
            ? explicitName
            : row.GetString("InferredName") ?? "";

    /// <remarks>
    /// ADOMD returns TMSCHEMA's booleans as <see cref="bool"/>, but not every provider agrees —
    /// <c>GetNullableLong</c> covers both forms, and an absent value counts as false.
    /// </remarks>
    private static bool Flag(DaxRow row, string column) =>
        row[column] switch
        {
            bool value => value,
            null => false,
            _ => row.GetNullableLong(column) is not (null or 0),
        };

    private static DaxCardinality Cardinality(int value) =>
        value == (int)DaxCardinality.One ? DaxCardinality.One : DaxCardinality.Many;

    /// <summary>Type 3 of <c>TMSCHEMA_COLUMNS</c> — the row-number column, internal.</summary>
    private const int RowNumberColumnType = 3;

    /// <summary>
    /// The type's name, from the TMSCHEMA code.
    /// </summary>
    /// <remarks>
    /// It returns <b>text</b>, not an enum of our own: what consumes this is contract generation
    /// and the divergence message, and both want the name. An enum would force a second map to get
    /// back to the name, and an unknown code would have no member.
    /// </remarks>
    private static string DataTypeName(int code) => code switch
    {
        2 => "String",
        6 => "Int64",
        8 => "Double",
        9 => "DateTime",
        10 => "Decimal",
        11 => "Boolean",
        17 => "Binary",
        19 => "Variant",
        _ => "Unknown",
    };

    private const string TablesQuery = "SELECT [ID], [Name] FROM $SYSTEM.TMSCHEMA_TABLES";

    private const string ColumnsQuery =
        "SELECT [ID], [TableID], [ExplicitName], [InferredName], [ExplicitDataType], [Type], "
        + "[IsHidden] FROM $SYSTEM.TMSCHEMA_COLUMNS";

    private const string MeasuresQuery =
        "SELECT [Name], [TableID], [DataType], [IsHidden] FROM $SYSTEM.TMSCHEMA_MEASURES";

    private const string RelationshipsQuery =
        "SELECT [FromTableID], [FromColumnID], [FromCardinality], [ToTableID], [ToColumnID], "
        + "[ToCardinality], [IsActive] FROM $SYSTEM.TMSCHEMA_RELATIONSHIPS";
}
