using System.Reflection;
using PowerLinq.DaxConverter.Attributes;

namespace PowerLinq.DaxConverter.Model;

/// <summary>
/// Checks hand-written contracts against the model, and reports what diverges.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the safety net that comes with generation.</b> A generated contract cannot diverge —
/// the names came from the model. But hand-written contracts still exist, and the model can change
/// after the code was generated; in both cases what is left is to check.
/// </para>
/// <para>
/// <b>It runs over a <see cref="DaxModelSchema"/>, not over a connection.</b> In CI the schema
/// comes from the versioned file, and the check touches no network — which is the property to
/// preserve.
/// </para>
/// <para>
/// It returns a list instead of throwing on the first divergence: whoever runs this in CI wants the
/// whole report, not its first line.
/// </para>
/// </remarks>
public static class DaxContractValidator
{
    /// <summary>
    /// Checks the types in an assembly that declare <c>[DaxTable]</c>.
    /// </summary>
    /// <param name="schema">The model's schema.</param>
    /// <param name="assembly">The assembly holding the contracts.</param>
    public static IReadOnlyList<DaxDivergence> Validate(DaxModelSchema schema, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return Validate(
            schema,
            assembly.GetTypes().Where(type => type.GetCustomAttribute<DaxTableAttribute>() is not null));
    }

    /// <summary>Checks the given types.</summary>
    /// <param name="schema">The model's schema.</param>
    /// <param name="contracts">The contract types.</param>
    public static IReadOnlyList<DaxDivergence> Validate(
        DaxModelSchema schema,
        IEnumerable<Type> contracts)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(contracts);

        var found = new List<DaxDivergence>();

        foreach (Type contract in contracts)
            ValidateContract(schema, contract, found);

        return found;
    }

    /// <summary>
    /// Checks a measure name.
    /// </summary>
    /// <param name="schema">The model's schema.</param>
    /// <param name="measure">The name, with or without brackets.</param>
    /// <param name="where">Where the name is written, for the message.</param>
    /// <remarks>
    /// Separate from the contract check because a measure name is <b>not</b> in an attribute — it
    /// is an argument of <c>MeasureAsync</c> or of <c>g.Measure&lt;T&gt;</c>, written at the call
    /// site, and reflection cannot reach it. The good way out is for generation to emit constants:
    /// then a typo becomes a compile error, and this becomes the net for whoever writes by hand.
    /// </remarks>
    public static DaxDivergence? ValidateMeasure(DaxModelSchema schema, string measure, string where)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (schema.HasMeasure(measure))
            return null;

        string bare = DaxModelSchema.Unbracket(measure);

        return new DaxDivergence(
            DaxDivergenceKind.MeasureNotInModel,
            where,
            $"The model has no measure named '{bare}'. {Closest(schema.Measures.Select(m => m.Name), bare)}");
    }

    /// <summary>
    /// Checks whether a navigation between two tables is expressible through <c>RELATED</c>.
    /// </summary>
    /// <param name="schema">The model's schema.</param>
    /// <param name="from">The table the navigation starts from — the iterating side.</param>
    /// <param name="to">The table whose column is to be read.</param>
    /// <param name="where">Where the navigation is declared, for the message.</param>
    /// <remarks>
    /// <para>
    /// <b>It exists for navigation between entities.</b> That attribute's design depends on this
    /// piece: with a generated contract the navigation comes out already resolved, with the
    /// cardinality checked at generation time. This is the check.
    /// </para>
    /// <para>
    /// <c>RELATED</c> crosses <b>many-to-one</b>, and only that. Starting from the <i>one</i> side
    /// is the case navigation raised with the RLS table example, and the server's error there says
    /// "the column does not exist or has no relationship" — a message that does not distinguish a
    /// missing column from a wrong direction.
    /// </para>
    /// </remarks>
    public static DaxDivergence? ValidateNavigation(
        DaxModelSchema schema,
        string from,
        string to,
        string where)
    {
        ArgumentNullException.ThrowIfNull(schema);

        IReadOnlyList<DaxSchemaRelationship> active = schema.ActivePathsBetween(from, to);

        if (active.Count > 1)
        {
            return new DaxDivergence(
                DaxDivergenceKind.AmbiguousRelationship,
                where,
                $"There is more than one active relationship between '{from}' and '{to}', so nothing "
                + "in the query says which one to use: "
                + string.Join("; ", active.Select(Describe))
                + ". Pick one explicitly, or deactivate the other in the model.");
        }

        if (active.Count == 0)
        {
            IReadOnlyList<DaxSchemaRelationship> inactive = schema.InactivePathsBetween(from, to);

            return inactive.Count > 0
                ? new DaxDivergence(
                    DaxDivergenceKind.InactiveRelationship,
                    where,
                    $"The only relationship between '{from}' and '{to}' is inactive, so filters do "
                    + $"not travel across it: {string.Join("; ", inactive.Select(Describe))}. An "
                    + "inactive relationship is only reachable through USERELATIONSHIP inside "
                    + "CALCULATE.")
                : new DaxDivergence(
                    DaxDivergenceKind.NoRelationship,
                    where,
                    $"The model has no relationship between '{from}' and '{to}'.");
        }

        DaxSchemaRelationship path = active[0];

        // RELATED goes from the MANY side to the ONE side. The side of `from` is the one that
        // iterates, so it has to be the many side — and the orientation depends on which end of the
        // relationship `from` is.
        bool fromIsMany = string.Equals(path.FromTable, from, StringComparison.OrdinalIgnoreCase)
            ? path.FromCardinality == DaxCardinality.Many
            : path.ToCardinality == DaxCardinality.Many;

        return fromIsMany
            ? null
            : new DaxDivergence(
                DaxDivergenceKind.UntraversableDirection,
                where,
                $"Navigating from '{from}' to '{to}' would need one-to-many, and RELATED only "
                + $"crosses many-to-one: {Describe(path)}. Read the value from the other side, or "
                + "express the filter as a filter-context argument instead.");
    }

    private static void ValidateContract(DaxModelSchema schema, Type contract, List<DaxDivergence> found)
    {
        string? tableName = contract.GetCustomAttribute<DaxTableAttribute>()?.TableName;

        if (string.IsNullOrEmpty(tableName))
            return;

        if (schema.Table(tableName) is null)
        {
            found.Add(new DaxDivergence(
                DaxDivergenceKind.TableNotInModel,
                contract.Name,
                $"The model has no table named '{tableName}'. "
                + Closest(schema.Tables.Select(t => t.Name), tableName)));

            // The columns are not checked: without the table, each of them would become a
            // divergence repeating the same cause, and the report for one wrong contract would be
            // twenty lines saying the same thing.
            return;
        }

        foreach (PropertyInfo property in contract.GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            // Navigation first: it can carry [DaxColumn] too — the foreign key — and what matters
            // about it is the relationship, not the column.
            if (property.GetCustomAttribute<DaxNavigationAttribute>() is not null)
            {
                ValidateNavigationProperty(schema, contract, property, tableName, found);
                continue;
            }

            if (property.GetCustomAttribute<DaxColumnAttribute>()?.ColumnReference is not { } reference)
                continue;

            ValidateColumnReference(schema, contract, property, reference, tableName, found);
        }
    }

    /// <summary>
    /// Checks a navigation property: the relationship exists, is unique, is active, and its
    /// direction is one <c>RELATED</c> crosses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It closes navigation's direction and ambiguity criteria without breaking the property
    /// that composition touches no network.</b> At translation time there is no way to know the
    /// cardinality — composing a query touches no network — so the one that knows is this check,
    /// which runs over the versioned schema.
    /// </para>
    /// <para>
    /// A navigation of <b>more than one hop</b> is checked hop by hop, and the message names what
    /// broke: <c>RELATED</c> crosses the whole chain, but only if <b>every</b> link is many-to-one,
    /// and saying merely "the navigation failed" would send people looking in the wrong place.
    /// </para>
    /// </remarks>
    private static void ValidateNavigationProperty(
        DaxModelSchema schema,
        Type contract,
        PropertyInfo property,
        string fromTable,
        List<DaxDivergence> found)
    {
        string where = $"{contract.Name}.{property.Name}";
        Type target = property.PropertyType;
        string toTable = target.GetCustomAttribute<DaxTableAttribute>()?.TableName ?? target.Name;

        if (ValidateNavigation(schema, fromTable, toTable, where) is { } divergence)
            found.Add(divergence);

        // The next hop, when the destination navigates too. Each link is checked against its own
        // relationship, not against the chain's starting table.
        foreach (PropertyInfo next in target.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (next.GetCustomAttribute<DaxNavigationAttribute>() is not null)
                ValidateNavigationProperty(schema, target, next, toTable, found);
        }
    }

    private static void ValidateColumnReference(
        DaxModelSchema schema,
        Type contract,
        PropertyInfo property,
        string reference,
        string contractTable,
        List<DaxDivergence> found)
    {
        // `[Total]` with no table is a query OUTPUT column — an extension of a SUMMARIZECOLUMNS, or
        // the name of a projection. It does not exist in the model and should not: checking it here
        // would produce a divergence in every aggregated-result contract in the project.
        if (!TrySplit(reference, out string? table, out string? column))
            return;

        table ??= contractTable;

        if (schema.Table(table) is not { } schemaTable)
        {
            found.Add(new DaxDivergence(
                DaxDivergenceKind.TableNotInModel,
                $"{contract.Name}.{property.Name}",
                $"The model has no table named '{table}', referenced by '{reference}'. "
                + Closest(schema.Tables.Select(t => t.Name), table)));

            return;
        }

        if (schema.HasColumn(table, column))
            return;

        found.Add(new DaxDivergence(
            DaxDivergenceKind.ColumnNotInModel,
            $"{contract.Name}.{property.Name}",
            $"Table '{table}' has no column named '{column}'. "
            + Closest(schemaTable.Columns.Select(c => c.Name), column)));
    }

    /// <summary>
    /// Splits <c>Table[Column]</c> into table and column, returning <see langword="false"/> when
    /// the reference is not a model column.
    /// </summary>
    /// <remarks>
    /// The table may come in single quotes — <c>'Ordem de Venda'[Qtd]</c> — which is how DAX
    /// qualifies a name with a space. The quotes are syntax, not part of the name.
    /// </remarks>
    private static bool TrySplit(string reference, out string? table, out string column)
    {
        table = null;
        column = "";

        int open = reference.IndexOf('[', StringComparison.Ordinal);

        if (open < 0 || !reference.EndsWith(']'))
            return false;

        column = reference[(open + 1)..^1];

        if (column.Length == 0)
            return false;

        // The single quotes are syntax, not part of the name: 'Ordem de Venda'[Qtd] is how DAX
        // qualifies a name with a space.
        table = reference[..open].Trim('\'');

        // An empty prefix is a query OUTPUT column — `[Total]`, an extension of a SUMMARIZECOLUMNS
        // or a projection name. It does not exist in the model and should not: checking it would
        // produce a divergence in every aggregated-result contract in the project.
        return table.Length > 0;
    }

    /// <summary>
    /// A "did you mean", when there is a candidate close enough.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A typo is the motivating case, and for it the whole list of names is worse than one guess: a
    /// model with three hundred columns would produce a message nobody reads.
    /// </para>
    /// <para>
    /// The distance is Levenshtein's, with a budget proportional to the name's length — a bad guess
    /// costs more than no guess, because it sends people looking in the wrong place.
    /// </para>
    /// </remarks>
    private static string Closest(IEnumerable<string> candidates, string name)
    {
        int budget = Math.Max(2, name.Length / 3);

        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (string candidate in candidates)
        {
            int distance = Distance(candidate, name);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best is not null && bestDistance <= budget
            ? $"Did you mean '{best}'?"
            : "";
    }

    private static int Distance(string a, string b)
    {
        // A single row of the matrix: the distance of each prefix depends only on the previous row,
        // and keeping the whole matrix here would mean allocating per candidate inside a loop over
        // the entire model.
        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (int j = 1; j <= b.Length; j++)
            {
                int substitution = previous[j - 1]
                                   + (char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1);

                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static string Describe(DaxSchemaRelationship relationship) =>
        $"{relationship.FromTable}[{relationship.FromColumn}] "
        + $"{Symbol(relationship.FromCardinality)}—{Symbol(relationship.ToCardinality)} "
        + $"{relationship.ToTable}[{relationship.ToColumn}]";

    private static string Symbol(DaxCardinality cardinality) =>
        cardinality == DaxCardinality.One ? "1" : "*";
}
