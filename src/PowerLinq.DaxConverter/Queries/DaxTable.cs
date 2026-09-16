using System.Linq.Expressions;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Mapping;

namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// Entry point for querying a DAX table. Represents the table in its unfiltered state.
/// </summary>
public sealed class DaxTable<T> : IDaxTable<T> where T : class
{
    private readonly IDaxQueryExecutor _executor;
    private readonly DaxPipeline _basePipeline;
    private readonly IPowerLinqLocalizer _localizer;

    /// <summary>Creates the table over the executor, with messages in English.</summary>
    public DaxTable(IDaxQueryExecutor executor)
        : this(executor, ResourceManagerPowerLinqLocalizer.English) { }

    internal DaxTable(IDaxQueryExecutor executor, IPowerLinqLocalizer localizer)
    {
        _executor = executor;
        _localizer = localizer;
        _basePipeline = new DaxPipeline(EntityMapper.GetTableName<T>(), typeof(T));
    }

    private DaxQuery<T> CreateQuery() => new(_executor, _basePipeline, _localizer);

    internal IDaxQueryExecutor Executor => _executor;
    internal string TableName => _basePipeline.TableName;
    internal IPowerLinqLocalizer Localizer => _localizer;

    /// <inheritdoc/>
    public DaxQuery<T> Where(Expression<Func<T, bool>> predicate) => CreateQuery().Where(predicate);

    /// <inheritdoc/>
    public DaxQuery<T> WhereIf(bool condition, Expression<Func<T, bool>> predicate) =>
        CreateQuery().WhereIf(condition, predicate);

    /// <inheritdoc/>
    public DaxQuery<T> WhereIf(string? value, Expression<Func<T, bool>> predicate) =>
        CreateQuery().WhereIf(value, predicate);

    /// <inheritdoc/>
    public DaxQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) =>
        CreateQuery().OrderBy(keySelector);

    /// <inheritdoc/>
    public DaxQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) =>
        CreateQuery().OrderByDescending(keySelector);

    /// <inheritdoc/>
    public DaxQuery<T> Take(int count) => CreateQuery().Take(count);

    /// <inheritdoc/>
    public DaxQuery<T> Skip(int count) => CreateQuery().Skip(count);

    /// <inheritdoc/>
    public DaxQuery<TResult> Select<TResult>(Expression<Func<T, TResult>> selector)
        where TResult : class => CreateQuery().Select(selector);

    /// <inheritdoc/>
    public DaxGroupedQuery<T, TKey> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector) =>
        CreateQuery().GroupBy(keySelector);

    /// <inheritdoc/>
    public DaxQuery<TResult> Aggregate<TResult>(
        Expression<Func<IDaxAggregate<T>, TResult>> selector) where TResult : class =>
        CreateQuery().Aggregate(selector);

    /// <inheritdoc/>
    public DaxQuery<T> Distinct() => CreateQuery().Distinct();

    /// <inheritdoc/>
    public Task<List<TValue>> DistinctValuesAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) =>
        CreateQuery().DistinctValuesAsync(selector, cancellationToken);

    /// <inheritdoc/>
    public Task<List<TValue>> ValuesAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) =>
        CreateQuery().ValuesAsync(selector, cancellationToken);

    /// <inheritdoc/>
    public Task<List<T>> ToListAsync(CancellationToken cancellationToken = default) =>
        CreateQuery().ToListAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<TValue?> MeasureAsync<TValue>(
        string measure,
        CancellationToken cancellationToken = default) =>
        CreateQuery().MeasureAsync<TValue>(measure, cancellationToken);

    /// <summary>
    /// The measure with the filter of the given columns <b>removed</b> —
    /// <c>CALCULATE([Measure], REMOVEFILTERS(column...))</c>. No columns removes the table's.
    /// </summary>
    /// <typeparam name="TValue">The result's type.</typeparam>
    /// <param name="measure">The measure's name, with or without brackets.</param>
    /// <param name="columns">The columns whose filter goes away. A column selector, <b>not</b> a predicate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <b>It is not on <see cref="IDaxTable{T}"/>, on purpose.</b> Adding a member to an
    /// already-published interface forces every existing implementation to gain a method it does
    /// not use — it is the same reason that made <c>IDaxRawQueryExecutor</c> a separate interface
    /// instead of a new member on <c>IDaxQueryExecutor</c>. Whoever only has the interface composes
    /// a <c>Where</c> first, and the query that comes back carries these methods.
    /// </remarks>
    public Task<TValue?> MeasureIgnoringAsync<TValue>(
        string measure,
        Expression<Func<T, object?>>[] columns,
        CancellationToken cancellationToken = default) =>
        CreateQuery().MeasureIgnoringAsync<TValue>(measure, columns, cancellationToken);

    /// <summary>
    /// The measure keeping the filter of <b>only</b> the given columns —
    /// <c>CALCULATE([Measure], ALLEXCEPT(Table, column...))</c>.
    /// </summary>
    /// <typeparam name="TValue">The result's type.</typeparam>
    /// <param name="measure">The measure's name, with or without brackets.</param>
    /// <param name="columns">The columns whose filter <b>stays</b>. At least one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<TValue?> MeasureKeepingOnlyAsync<TValue>(
        string measure,
        Expression<Func<T, object?>>[] columns,
        CancellationToken cancellationToken = default) =>
        CreateQuery().MeasureKeepingOnlyAsync<TValue>(measure, columns, cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<T> AsAsyncEnumerable(CancellationToken cancellationToken = default) =>
        CreateQuery().AsAsyncEnumerable(cancellationToken);

    /// <inheritdoc/>
    public Task<DaxPage<T>> ToPagedListAsync(CancellationToken cancellationToken = default) =>
        CreateQuery().ToPagedListAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default) =>
        CreateQuery().FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<T> FirstAsync(CancellationToken cancellationToken = default) =>
        CreateQuery().FirstAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<T[]> ToArrayAsync(CancellationToken cancellationToken = default) =>
        CreateQuery().ToArrayAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<Dictionary<TKey, T>> ToDictionaryAsync<TKey>(
        Func<T, TKey> keySelector,
        CancellationToken cancellationToken = default) where TKey : notnull =>
        CreateQuery().ToDictionaryAsync(keySelector, cancellationToken);

    /// <inheritdoc/>
    public Task<Dictionary<TKey, TValue>> ToDictionaryAsync<TKey, TValue>(
        Func<T, TKey> keySelector,
        Func<T, TValue> valueSelector,
        CancellationToken cancellationToken = default) where TKey : notnull =>
        CreateQuery().ToDictionaryAsync(keySelector, valueSelector, cancellationToken);

    /// <inheritdoc/>
    public Task<T> SingleAsync(CancellationToken cancellationToken = default) =>
        CreateQuery().SingleAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<T?> SingleOrDefaultAsync(CancellationToken cancellationToken = default) =>
        CreateQuery().SingleOrDefaultAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        CreateQuery().CountAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<long> LongCountAsync(CancellationToken cancellationToken = default) =>
        CreateQuery().LongCountAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<TValue> SumAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) =>
        CreateQuery().SumAsync(selector, cancellationToken);

    /// <inheritdoc/>
    public Task<TValue> MinAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) =>
        CreateQuery().MinAsync(selector, cancellationToken);

    /// <inheritdoc/>
    public Task<TValue> MaxAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) =>
        CreateQuery().MaxAsync(selector, cancellationToken);

    /// <inheritdoc/>
    public Task<double> AverageAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) =>
        CreateQuery().AverageAsync(selector, cancellationToken);

    /// <inheritdoc/>
    public Task<double?> AverageOrDefaultAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) =>
        CreateQuery().AverageOrDefaultAsync(selector, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> AnyAsync(CancellationToken cancellationToken = default) =>
        CreateQuery().AnyAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        CreateQuery().AnyAsync(predicate, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> AllAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        CreateQuery().AllAsync(predicate, cancellationToken);

    /// <inheritdoc/>
    public DaxQuery<TResult> Join<TInner, TKey, TResult>(
        DaxQuery<TInner> inner,
        Expression<Func<T, TKey>> outerKeySelector,
        Expression<Func<TInner, TKey>> innerKeySelector,
        Expression<Func<T, TInner, TResult>> resultSelector)
        where TInner : class
        where TResult : class =>
        CreateQuery().Join(inner, outerKeySelector, innerKeySelector, resultSelector);

    /// <inheritdoc/>
    public DaxQuery<TResult> Join<TInner, TKey, TResult>(
        IDaxTable<TInner> inner,
        Expression<Func<T, TKey>> outerKeySelector,
        Expression<Func<TInner, TKey>> innerKeySelector,
        Expression<Func<T, TInner, TResult>> resultSelector)
        where TInner : class
        where TResult : class =>
        CreateQuery().Join(inner, outerKeySelector, innerKeySelector, resultSelector);
}
