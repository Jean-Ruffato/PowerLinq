namespace PowerLinq.DaxConverter.Model;

/// <summary>
/// The metadata of a semantic model: tables, columns, measures and relationships.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is data, not a connection.</b> <see cref="DaxSchemaReader"/> produces it from the
/// endpoint, and from there on everything that consumes it — generation and validation — works
/// over this object, which serializes to a file. That is what preserves the central property:
/// composing a query touches no network, and neither does checking it.
/// </para>
/// <para>
/// Lookup is by name, and case-insensitive: DAX does not distinguish case in table or column
/// names, so a case-sensitive index would report a column the server accepts as missing — the
/// worst way to be wrong in a tool whose reason for existing is to point out divergence.
/// </para>
/// <para>
/// <b>A class, not a <c>record</c>, on purpose.</b> A record's synthesized equality compares field
/// by field with <c>EqualityComparer&lt;T&gt;.Default</c>, and for a list that is <b>reference</b>
/// equality — two equal schemas would compare as different. The internal index would make it
/// worse: it would enter the comparison too. It is the same trap that led to dropping the tree as
/// a cache key, and here the way out is not to promise equality at all.
/// </para>
/// </remarks>
public sealed class DaxModelSchema
{
    private readonly Dictionary<string, DaxSchemaTable> _byName;

    /// <summary>Builds the schema.</summary>
    /// <param name="tables">The tables.</param>
    /// <param name="measures">The measures, from every table.</param>
    /// <param name="relationships">The relationships.</param>
    public DaxModelSchema(
        IReadOnlyList<DaxSchemaTable> tables,
        IReadOnlyList<DaxSchemaMeasure> measures,
        IReadOnlyList<DaxSchemaRelationship> relationships)
    {
        Tables = tables ?? throw new ArgumentNullException(nameof(tables));
        Measures = measures ?? throw new ArgumentNullException(nameof(measures));
        Relationships = relationships ?? throw new ArgumentNullException(nameof(relationships));

        // A repeated name belongs to the model, not to us: ToDictionary would throw with a message
        // that does not say where it came from. The first one stays, and validation is what reports
        // the rest.
        _byName = new Dictionary<string, DaxSchemaTable>(StringComparer.OrdinalIgnoreCase);

        foreach (DaxSchemaTable table in Tables)
            _byName.TryAdd(table.Name, table);
    }

    /// <summary>The tables.</summary>
    public IReadOnlyList<DaxSchemaTable> Tables { get; }

    /// <summary>The measures, from every table.</summary>
    public IReadOnlyList<DaxSchemaMeasure> Measures { get; }

    /// <summary>The relationships.</summary>
    public IReadOnlyList<DaxSchemaRelationship> Relationships { get; }

    /// <summary>An empty schema — no tables, measures or relationships.</summary>
    public static DaxModelSchema Empty { get; } = new([], [], []);

    /// <summary>The table with that name, or <see langword="null"/>.</summary>
    /// <param name="name">The table's name.</param>
    public DaxSchemaTable? Table(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Whether the column exists on the table.</summary>
    /// <param name="table">The table's name.</param>
    /// <param name="column">The column's name.</param>
    public bool HasColumn(string table, string column) =>
        Table(table)?.Columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))
        ?? false;

    /// <summary>Whether the measure exists, on any table.</summary>
    /// <param name="measure">The measure's name, with or without brackets.</param>
    /// <remarks>
    /// It accepts both forms because both show up: the model stores <c>Total Vendas</c> and whoever
    /// writes DAX writes <c>[Total Vendas]</c>. Demanding one of them would turn a difference of
    /// notation into a false positive divergence.
    /// </remarks>
    public bool HasMeasure(string measure)
    {
        string bare = Unbracket(measure);

        return Measures.Any(m => string.Equals(m.Name, bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The <b>active</b> relationships that link the two tables directly, in either direction.
    /// </summary>
    /// <param name="left">One of the tables.</param>
    /// <param name="right">The other.</param>
    /// <remarks>
    /// More than one result is an <b>ambiguous</b> relationship: there are two active paths between
    /// the same tables, and nothing in the query says which to use. The caller decides what to do
    /// with that — here the whole list is returned, because picking a path silently is precisely
    /// the behaviour that is the problem.
    /// </remarks>
    public IReadOnlyList<DaxSchemaRelationship> ActivePathsBetween(string left, string right) =>
        [.. Relationships.Where(r =>
            r.IsActive
            && ((Same(r.FromTable, left) && Same(r.ToTable, right))
                || (Same(r.FromTable, right) && Same(r.ToTable, left))))];

    /// <summary>
    /// The <b>inactive</b> relationships between the two tables.
    /// </summary>
    /// <param name="left">One of the tables.</param>
    /// <param name="right">The other.</param>
    /// <remarks>
    /// Consulted when there is no active path: the message "there is no relationship" is misleading
    /// when one exists and is switched off, and the way out for the user is a different one —
    /// <c>USERELATIONSHIP</c>, not creating a relationship.
    /// </remarks>
    public IReadOnlyList<DaxSchemaRelationship> InactivePathsBetween(string left, string right) =>
        [.. Relationships.Where(r =>
            !r.IsActive
            && ((Same(r.FromTable, left) && Same(r.ToTable, right))
                || (Same(r.FromTable, right) && Same(r.ToTable, left))))];

    /// <summary>The name without the DAX reference brackets.</summary>
    internal static string Unbracket(string name) =>
        name.StartsWith('[') && name.EndsWith(']')
            ? name[1..^1]
            : name;

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
