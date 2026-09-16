using System.Collections.Concurrent;
using PowerLinq.DaxConverter.Interfaces;

namespace PowerLinq.DaxConverter.Context;

/// <summary>
/// The unit of access to the DAX tables. Like a DbContext, it takes its dependencies through the
/// constructor and keeps one instance of each table per context.
/// </summary>
/// <remarks>
/// <para>
/// <b>It does not implement <see cref="IAsyncDisposable"/>, on purpose.</b> An earlier version did,
/// because it created the executor by reflection and therefore owned it. Once dependency injection
/// was adopted, the executor became something the context <b>receives</b>, and its lifetime belongs
/// to the container. Disposing an injected dependency is a classic DI mistake: the container still
/// considers it alive, and in the case of a singleton executor the context would tear down the
/// instance for the whole application.
/// </para>
/// <para>
/// If the context ever starts creating resources of its own again, then it should dispose those —
/// and only those.
/// </para>
/// </remarks>
public class DaxContext
{
    private readonly IDaxQueryExecutor _executor;
    private readonly IDaxTableFactory _tableFactory;

    /// <remarks>
    /// Concurrent because the context is registered per scope, and the natural pattern on a
    /// dashboard is firing the queries in parallel from the <b>same</b> context
    /// (<c>Task.WhenAll</c>). Two simultaneous calls to <see cref="Set{TEntity}"/> with different
    /// types on a <see cref="Dictionary{TKey,TValue}"/> can corrupt the internal structure during
    /// a resize — an intermittent symptom, typically an <see cref="IndexOutOfRangeException"/> or
    /// an infinite loop burning CPU.
    /// </remarks>
    private readonly ConcurrentDictionary<Type, object> _tables = new();

    /// <summary>Takes its dependencies through the constructor, in the DI style.</summary>
    protected DaxContext(IDaxQueryExecutor executor, IDaxTableFactory tableFactory)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _tableFactory = tableFactory ?? throw new ArgumentNullException(nameof(tableFactory));
    }

    /// <summary>Gets the table of the given type, creating it once per context.</summary>
    /// <remarks>
    /// Under concurrency, <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,Func{TKey,TValue})"/>
    /// may run the factory more than once, and only one result is published. That is harmless
    /// here: <c>DaxTable&lt;T&gt;</c> is immutable and cheap to create, and every caller still
    /// receives the same instance.
    /// </remarks>
    public IDaxTable<TEntity> Set<TEntity>() where TEntity : class =>
        (IDaxTable<TEntity>)_tables.GetOrAdd(
            typeof(TEntity),
            _ => _tableFactory.Create<TEntity>(_executor));

    /// <summary>
    /// <b>Escape hatch:</b> sends hand-written DAX and materializes the rows into
    /// <typeparamref name="TRow"/>.
    /// </summary>
    /// <typeparam name="TRow">The contract of each row, mapped like any other.</typeparam>
    /// <param name="dax">The query, starting at <c>EVALUATE</c> or <c>DEFINE</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">The query is empty or only whitespace.</exception>
    /// <remarks>
    /// <para>
    /// <b>The name is long on purpose.</b> A translation library eventually needs an escape, and
    /// the question is not whether it exists — it is whether it is declared or reinvented. Before
    /// this was here, reading a measure went through <c>ExecuteCountAsync</c>, a method whose name
    /// says "count": the detour existed, unnamed and undocumented, and nobody reading the call
    /// would notice.
    /// </para>
    /// <para>
    /// <b>What is lost.</b> Everything the translation guarantees: no column check against the
    /// contract, no distinction between measure and column, no resolution of the ordering against
    /// the result, and syntax errors only show up on the server. Composition is lost too — the
    /// result is not a query, it is a list.
    /// </para>
    /// <para>
    /// <b>What is kept.</b> Materialization is the same as everywhere else, through
    /// <c>EntityMapper</c>: the same attributes, the same type table, the same invariant-culture
    /// conversion, the same error naming property and column. Duplicating that here would create
    /// two lists of "what is supported" that would diverge the first time one of them gained a new
    /// type.
    /// </para>
    /// </remarks>
    public Task<List<TRow>> FromRawDaxAsync<TRow>(
        string dax,
        CancellationToken cancellationToken = default) where TRow : class =>
        _executor.ExecuteAsync<TRow>(Validated(dax), cancellationToken);

    /// <summary>
    /// Runs a compiled query (<see cref="Queries.DaxCompiledQuery"/>), binding the value.
    /// </summary>
    /// <typeparam name="TRow">The table's entity.</typeparam>
    /// <typeparam name="TParam">The type of the parameter's value.</typeparam>
    /// <param name="query">The compiled query, typically a <c>static readonly</c> field.</param>
    /// <param name="value">The parameter's value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// It lives here, and not on <c>IDaxTable</c>, because the context is what knows how to get the
    /// table from the entity (<see cref="Set{TEntity}"/>) — the caller passes the query and the
    /// value, not the table.
    /// </para>
    /// <para>
    /// <b>It is not the escape hatch, despite the neighbourhood.</b>
    /// <see cref="FromRawDaxAsync{TRow}"/> accepts text nobody checked; here the DAX was
    /// <b>produced by the translation</b>, with all of its checks, and what is reused is the result
    /// of that work. All the two share is the last step, executing a text that is already done.
    /// </para>
    /// </remarks>
    public Task<List<TRow>> FromCompiledAsync<TRow, TParam>(
        Queries.DaxCompiledQuery<TRow, TParam> query,
        TParam value,
        CancellationToken cancellationToken = default) where TRow : class
    {
        ArgumentNullException.ThrowIfNull(query);

        return _executor.ExecuteAsync<TRow>(
            query.ToDaxString(Set<TRow>(), value), cancellationToken);
    }

    /// <summary>The same execution, for a compiled query with two parameters.</summary>
    /// <typeparam name="TRow">The table's entity.</typeparam>
    /// <typeparam name="TParam1">The type of the first parameter's value.</typeparam>
    /// <typeparam name="TParam2">The type of the second parameter's value.</typeparam>
    /// <param name="query">The compiled query.</param>
    /// <param name="first">The first parameter's value.</param>
    /// <param name="second">The second parameter's value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<List<TRow>> FromCompiledAsync<TRow, TParam1, TParam2>(
        Queries.DaxCompiledQuery<TRow, TParam1, TParam2> query,
        TParam1 first,
        TParam2 second,
        CancellationToken cancellationToken = default) where TRow : class
    {
        ArgumentNullException.ThrowIfNull(query);

        return _executor.ExecuteAsync<TRow>(
            query.ToDaxString(Set<TRow>(), first, second), cancellationToken);
    }

    /// <summary>
    /// The same escape hatch, for a query that returns <b>a single value</b> — the shape of
    /// <c>EVALUATE ROW("x", &lt;expression&gt;)</c>.
    /// </summary>
    /// <typeparam name="TValue">The result's type.</typeparam>
    /// <param name="dax">The query, starting at <c>EVALUATE</c> or <c>DEFINE</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">The query is empty or only whitespace.</exception>
    /// <remarks>
    /// <c>BLANK</c> comes back as <see langword="null"/>, not as zero — the same decision as
    /// <c>MinAsync</c> and <c>MeasureAsync</c>, because flattening it would erase the difference
    /// between "summed to zero" and "there was no row".
    /// </remarks>
    public async Task<TValue?> FromRawDaxScalarAsync<TValue>(
        string dax,
        CancellationToken cancellationToken = default)
    {
        object? value = await _executor.ExecuteScalarAsync(Validated(dax), cancellationToken);

        return value is null
            ? default
            : (TValue)Mapping.DaxValueConverter.Convert(
                value,
                Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue),
                "raw DAX",
                "raw DAX",
                Localization.ResourceManagerPowerLinqLocalizer.English);
    }

    /// <remarks>
    /// The only check that belongs here. Validating more would mean reimplementing the server's
    /// DAX parser — and the whole reason this door exists is to accept what the translation does
    /// not express.
    /// </remarks>
    private static string Validated(string dax) =>
        string.IsNullOrWhiteSpace(dax)
            ? throw new ArgumentException("A DAX query is required.", nameof(dax))
            : dax;
}
