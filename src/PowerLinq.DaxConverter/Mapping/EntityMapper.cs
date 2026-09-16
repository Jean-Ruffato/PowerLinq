using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Data;
using System.Reflection;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Syntax;
using PowerLinq.DaxConverter.Translators;

namespace PowerLinq.DaxConverter.Mapping;

/// <summary>
/// Resolves an entity's mapping — table and column names — and materializes the result.
/// </summary>
/// <remarks>
/// The per-type mapping is cached and immutable; value conversion is centralized in
/// <see cref="DaxValueConverter"/>, so that both read paths have exactly the same semantics.
/// </remarks>
public static class EntityMapper
{
    /// <summary>DAX table name of <typeparamref name="T"/>.</summary>
    public static string GetTableName<T>() => GetTableName(typeof(T));

    /// <summary>
    /// DAX table name of the type: the value of <c>[DaxTable]</c>, or the type's own name.
    /// </summary>
    /// <remarks>
    /// Public because the translation needs to resolve the table of a type that is not the query's
    /// entity — that is the case of <c>Count&lt;TOther&gt;()</c>, which counts rows of another
    /// table of the model.
    /// </remarks>
    public static string GetTableName(Type type)
    {
        DaxTableAttribute? attr = type.GetCustomAttribute<DaxTableAttribute>();

        return attr?.TableName ?? type.Name;
    }

    /// <summary>
    /// Per-type mapping cache. Reflecting over a type always gives the same result, and the
    /// returned dictionary is immutable, so keeping it is safe.
    /// </summary>
    /// <remarks>
    /// Without the cache, reflection ran on every query execution: 8,081 ns and 3,680 B per call,
    /// a <b>fixed</b> cost. Irrelevant when diluted across a large result, dominant in a query that
    /// brings back a single row — and single-row queries are the majority on a dashboard (a KPI, a
    /// total, a filter option).
    /// </remarks>
    private static readonly ConcurrentDictionary<Type, FrozenDictionary<string, DaxColumnMapping>> ColumnMappings = new();

    /// <summary>Per-type activation plan cache, for the same reason as the mapping cache.</summary>
    private static readonly ConcurrentDictionary<Type, DaxActivation> Activations = new();

    /// <summary>How to create an instance of the type — see <see cref="DaxActivation"/>.</summary>
    private static DaxActivation GetActivation(Type type) =>
        Activations.GetOrAdd(type, static declaring => DaxActivation.For(declaring, GetColumnMappings(declaring)));

    /// <summary>
    /// Column reference to the property that receives it, in the forms the server may return the
    /// name in.
    /// </summary>
    /// <remarks>
    /// It returns a <see cref="FrozenDictionary{TKey,TValue}"/>, not a <c>Dictionary</c>: the value
    /// is shared across every query for the type, so mutation by the caller would corrupt the
    /// cache — the immutable type makes that impossible rather than merely unlikely. Lookup is also
    /// faster, which matters because it happens once per column per row.
    /// </remarks>
    public static FrozenDictionary<string, DaxColumnMapping> GetColumnMappings(Type type) =>
        ColumnMappings.GetOrAdd(type, static declaring => BuildColumnMappings(declaring));

    private static FrozenDictionary<string, DaxColumnMapping> BuildColumnMappings(Type type)
    {
        var mappings = new Dictionary<string, DaxColumnMapping>(StringComparer.OrdinalIgnoreCase);

        // Outside the loop: the table name belongs to the type, not to the property.
        string tableName = GetTableName(type);

        foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite)
                continue;

            // A navigation is not a column, and cannot be materialized: `EVALUATE Fact` returns the
            // fact's columns, not the dimension's entity. Without this guard the property would go
            // in as a column `Fact[Exporter]`, which the server does not have — and the symptom
            // would be materialization failing on an unsupported type, pointing at a problem that
            // is not the one that happened. Reading a dimension value is the job of a projection,
            // not of the entity.
            if (DaxNavigation.IsNavigation(prop))
                continue;

            var mapping = new DaxColumnMapping(prop);
            DaxColumnAttribute? attr = prop.GetCustomAttribute<DaxColumnAttribute>();

            if (attr is not null)
            {
                // The attribute's spelling goes in as it came — it is the one the generator emits
                // in the DAX.
                mappings[attr.ColumnReference] = mapping;

                // And the OTHER spelling of the same column goes in alongside it. The attribute's
                // string serves two purposes: it becomes the reference text in the DAX and it is
                // the key for reading the result. ADOMD returns the column name with the table
                // UNQUOTED (`T[C]`), and the DAX convention — which the library itself emits — is
                // `'T'[C]`. Registering only the literal made anyone who wrote the quoted form
                // generate correct DAX and materialize everything with the type's default: no
                // error, no wrong query, and an empty screen. A table with a space had no spelling
                // that served both sides.
                if (DaxIdentifier.TableOf(attr.ColumnReference) is { } table
                    && DaxIdentifier.ColumnOf(attr.ColumnReference) is { } column)
                {
                    mappings[DaxIdentifier.Column(table, column)] = mapping;
                    mappings[DaxIdentifier.UnquotedColumn(table, column)] = mapping;
                }
            }
            else
            {
                mappings[prop.Name] = mapping;
                mappings[$"[{prop.Name}]"] = mapping;

                // Both forms of the qualified reference. The query asks with the table quoted
                // (`'Ordem de Venda'[Qtd]`), but the server usually returns the column name
                // unquoted in the result metadata — registering both makes the mapping match
                // whichever arrives. For a simple name the two coincide.
                mappings[DaxIdentifier.Column(tableName, prop.Name)] = mapping;
                mappings[DaxIdentifier.UnquotedColumn(tableName, prop.Name)] = mapping;
            }
        }

        return mappings.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Materializes one row of an <see cref="IDataRecord"/> into the typed entity.</summary>
    /// <param name="record">The reader's current row.</param>
    /// <param name="mappings">Column reference to property, from <see cref="GetColumnMappings"/>.</param>
    /// <param name="localizer">
    /// Language of the conversion error messages. When absent, English is used — the same default
    /// as <see cref="DaxRow"/> and <c>DaxTable&lt;T&gt;</c>.
    /// </param>
    public static T MapRow<T>(
        IDataRecord record,
        FrozenDictionary<string, DaxColumnMapping> mappings,
        IPowerLinqLocalizer? localizer = null)
        where T : class
    {
        IPowerLinqLocalizer messages = localizer ?? ResourceManagerPowerLinqLocalizer.English;
        DaxActivation activation = GetActivation(typeof(T));

        if (activation.Create is null)
            return ByConstructor<T>(record, activation, messages);

        var entity = (T)activation.Create();

        for (int i = 0; i < record.FieldCount; i++)
        {
            string columnName = record.GetName(i);
            if (!mappings.TryGetValue(columnName, out DaxColumnMapping? mapping) || record.IsDBNull(i))
                continue;

            SetValue(entity, mapping, record.GetValue(i), columnName, messages);
        }

        return entity;
    }

    /// <summary>
    /// Materializes by passing the columns to the constructor — the path of a positional
    /// <c>record</c>, an anonymous type and an immutable DTO.
    /// </summary>
    /// <remarks>
    /// A column absent from the result leaves the parameter at its <c>default</c>, which is the
    /// same semantics as the property path: there the property is simply not assigned.
    /// </remarks>
    private static T ByConstructor<T>(
        IDataRecord record,
        DaxActivation activation,
        IPowerLinqLocalizer messages)
        where T : class
    {
        object?[] arguments = [.. activation.Defaults];

        for (int i = 0; i < record.FieldCount; i++)
        {
            string columnName = record.GetName(i);

            if (!activation.ParameterByColumn.TryGetValue(columnName, out int position)
                || record.IsDBNull(i))
            {
                continue;
            }

            arguments[position] = ConvertArgument(
                record.GetValue(i), activation.ParameterTypes[position], columnName, messages);
        }

        return (T)activation.Constructor!.Invoke(arguments);
    }

    /// <summary>
    /// Converts the cell to the parameter's type, through the same path as the property one.
    /// </summary>
    private static object? ConvertArgument(
        object value,
        Type parameterType,
        string columnReference,
        IPowerLinqLocalizer localizer)
    {
        Type target = Nullable.GetUnderlyingType(parameterType) ?? parameterType;

        return DaxValueConverter.Convert(value, target, columnReference, columnReference, localizer);
    }

    /// <summary>Maps a <see cref="DaxResult"/> (columns + rows) onto typed entities.</summary>
    public static List<T> MapResult<T>(DaxResult result, IPowerLinqLocalizer? localizer = null)
        where T : class
    {
        FrozenDictionary<string, DaxColumnMapping> mappings = GetColumnMappings(typeof(T));
        var entities = new List<T>(result.Rows.Count);

        foreach (DaxRow row in result.Rows)
            entities.Add(MapRow<T>(row, mappings, localizer));

        return entities;
    }

    /// <summary>
    /// The version of <see cref="MapRow{T}(IDataRecord, FrozenDictionary{string, DaxColumnMapping}, IPowerLinqLocalizer)"/>
    /// for a <see cref="DaxRow"/> (a column-to-value dictionary), used by the pool path.
    /// </summary>
    /// <remarks>
    /// It iterates the row's cells, not the mapping's keys, mirroring the <see cref="IDataRecord"/>
    /// path. The same set of assignments comes out with fewer lookups: a property with no attribute
    /// registers <b>four</b> keys — the name, <c>[name]</c> and the two qualified forms — so
    /// walking the mapping meant four lookups per property to find the single cell that exists.
    /// </remarks>
    public static T MapRow<T>(
        DaxRow row,
        FrozenDictionary<string, DaxColumnMapping> mappings,
        IPowerLinqLocalizer? localizer = null)
        where T : class
    {
        IPowerLinqLocalizer messages = localizer ?? ResourceManagerPowerLinqLocalizer.English;
        DaxActivation activation = GetActivation(typeof(T));

        if (activation.Create is null)
            return ByConstructor<T>(row, activation, messages);

        var entity = (T)activation.Create();

        foreach ((string columnReference, object? value) in row.Cells)
        {
            if (value is null || !mappings.TryGetValue(columnReference, out DaxColumnMapping? mapping))
                continue;

            SetValue(entity, mapping, value, columnReference, messages);
        }

        return entity;
    }

    /// <summary>The constructor path for a <see cref="DaxRow"/>.</summary>
    private static T ByConstructor<T>(
        DaxRow row,
        DaxActivation activation,
        IPowerLinqLocalizer messages)
        where T : class
    {
        object?[] arguments = [.. activation.Defaults];

        foreach ((string columnReference, object? value) in row.Cells)
        {
            if (value is null
                || !activation.ParameterByColumn.TryGetValue(columnReference, out int position))
            {
                continue;
            }

            arguments[position] = ConvertArgument(
                value, activation.ParameterTypes[position], columnReference, messages);
        }

        return (T)activation.Constructor!.Invoke(arguments);
    }

    /// <summary>
    /// Converts and assigns one cell. The conversion is centralized in
    /// <see cref="DaxValueConverter"/> so that both read paths — the <see cref="IDataRecord"/> of
    /// the direct executor and the <see cref="DaxRow"/> of the pool — have exactly the same
    /// semantics.
    /// </summary>
    private static void SetValue(
        object entity,
        DaxColumnMapping mapping,
        object value,
        string columnReference,
        IPowerLinqLocalizer localizer) =>
        mapping.Set(
            entity,
            DaxValueConverter.Convert(
                value, mapping.TargetType, mapping.Property, columnReference, localizer));
}
