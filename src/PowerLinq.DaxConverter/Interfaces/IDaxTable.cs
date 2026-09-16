using System.Linq.Expressions;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.DaxConverter.Interfaces;

/// <summary>Queryable contract of a DAX table.</summary>
/// <remarks>
/// The operators also exist on <see cref="DaxQuery{T}"/>, the return of <see cref="Where"/> —
/// chaining from the table or from a filter comes to the same thing. The composition order is
/// fixed (<c>Where</c> → <c>OrderBy</c> → <c>Skip</c> → <c>Take</c>); outside it the call is
/// refused, rather than generating DAX whose semantics differ from LINQ's.
/// </remarks>
public interface IDaxTable<T> where T : class
{
    /// <summary>Filters the rows. Chained, it combines the predicates with <c>&amp;&amp;</c>.</summary>
    /// <remarks>
    /// A predicate over a column of <b>another</b> table of the model is accepted and emitted as
    /// filter context (<c>CALCULATETABLE</c>), not as a <c>FILTER</c> over the entity's table —
    /// which would be a DAX error.
    /// </remarks>
    DaxQuery<T> Where(Expression<Func<T, bool>> predicate);

    /// <summary>Applies the predicate only when <paramref name="condition"/> is true.</summary>
    DaxQuery<T> WhereIf(bool condition, Expression<Func<T, bool>> predicate);

    /// <summary>
    /// Applies the predicate only when <paramref name="value"/> has content — not null, not empty,
    /// not only whitespace. A convenience for a filter coming from an optional text field.
    /// </summary>
    DaxQuery<T> WhereIf(string? value, Expression<Func<T, bool>> predicate);

    /// <summary>Sorts ascending — <c>ORDER BY ... ASC</c>.</summary>
    DaxQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector);

    /// <summary>Sorts descending — <c>ORDER BY ... DESC</c>.</summary>
    DaxQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector);

    /// <summary>
    /// Limits the number of rows — <c>TOPN</c>, which takes the ordering as its own argument.
    /// It must be the last window operator.
    /// </summary>
    DaxQuery<T> Take(int count);

    /// <summary>
    /// Discards the first rows — <c>EXCEPT(..., TOPN(n, ...))</c>. It requires an ordering, since
    /// pagination without order is not deterministic.
    /// </summary>
    DaxQuery<T> Skip(int count);

    /// <summary>
    /// Projects onto another contract — <c>SELECTCOLUMNS</c>. The selector has to be an object
    /// initializer.
    /// </summary>
    DaxQuery<TResult> Select<TResult>(Expression<Func<T, TResult>> selector)
        where TResult : class;

    /// <summary>Groups by a key; the following <c>Select</c> defines the aggregations.</summary>
    DaxGroupedQuery<T, TKey> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector);

    /// <summary>
    /// Aggregates without a key: a single row with the extension columns. Each column's name comes
    /// from the property name, and <c>[DaxColumn]</c> is there to rename it.
    /// </summary>
    DaxQuery<TResult> Aggregate<TResult>(Expression<Func<IDaxAggregate<T>, TResult>> selector)
        where TResult : class;

    /// <summary>Removes duplicate rows on the server — <c>DISTINCT</c>.</summary>
    DaxQuery<T> Distinct();

    /// <summary>
    /// The distinct values of a column, with no intermediate contract — the option list of a
    /// filter.
    /// </summary>
    Task<List<TValue>> DistinctValuesAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The values of a column, with no intermediate contract and no deduplication — the scalar
    /// projection.
    /// </summary>
    Task<List<TValue>> ValuesAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default);

    /// <summary>Runs the query and materializes every row.</summary>
    Task<List<T>> ToListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the query and returns the rows as they arrive, without materializing the list.
    /// It requires an executor that also implements <c>IDaxStreamingQueryExecutor</c>.
    /// </summary>
    IAsyncEnumerable<T> AsAsyncEnumerable(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the query and returns the page <b>and</b> the total of the whole set, in a single
    /// query. It requires an executor that also implements <c>IDaxRawQueryExecutor</c>.
    /// </summary>
    Task<DaxPage<T>> ToPagedListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a <b>model measure</b>, with the composed filters entering as filter context
    /// (<c>CALCULATE</c>). A measure is not a column: it already is the aggregation, and it is not
    /// iterated.
    /// </summary>
    Task<TValue?> MeasureAsync<TValue>(string measure, CancellationToken cancellationToken = default);

    /// <summary>First row, or <see langword="null"/> when there is none.</summary>
    Task<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default);

    /// <summary>First row; throws when the result is empty.</summary>
    Task<T> FirstAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs the query and materializes every row as an array.</summary>
    Task<T[]> ToArrayAsync(CancellationToken cancellationToken = default);

    /// <summary>Materializes every row indexed by the key. The selector runs on the client.</summary>
    Task<Dictionary<TKey, T>> ToDictionaryAsync<TKey>(
        Func<T, TKey> keySelector,
        CancellationToken cancellationToken = default) where TKey : notnull;

    /// <summary>Materializes every row indexed by the key, with the value projected. The selectors run on the client.</summary>
    Task<Dictionary<TKey, TValue>> ToDictionaryAsync<TKey, TValue>(
        Func<T, TKey> keySelector,
        Func<T, TValue> valueSelector,
        CancellationToken cancellationToken = default) where TKey : notnull;

    /// <summary>The single row of the result; throws when there are zero or more than one.</summary>
    Task<T> SingleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The single row of the result, or <see langword="null"/> when there is none; it still throws
    /// when there is more than one.
    /// </summary>
    Task<T?> SingleOrDefaultAsync(CancellationToken cancellationToken = default);

    /// <summary>Counts the rows on the server — <c>COUNTROWS</c>, without fetching them.</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>Counts the rows on the server, returning a <see cref="long"/>.</summary>
    Task<long> LongCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Sums the column on the server — <c>SUMX</c>. Over no rows it returns the neutral zero.</summary>
    Task<TValue> SumAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Smallest value of the column on the server — <c>MINX</c>. Over no rows it returns
    /// <see langword="null"/> if <typeparamref name="TValue"/> accepts null, and throws if it does
    /// not.
    /// </summary>
    Task<TValue> MinAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default);

    /// <summary>Largest value of the column on the server — <c>MAXX</c>. Same empty semantics as <c>MinAsync</c>.</summary>
    Task<TValue> MaxAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Average of the column on the server — <c>AVERAGEX</c>. It returns a <see cref="double"/>
    /// rather than the column's type, because the average of an integer column is not an integer.
    /// Throws when there is no row.
    /// </summary>
    Task<double> AverageAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default);

    /// <summary>Average of the column on the server, returning <see langword="null"/> when there is no row.</summary>
    Task<double?> AverageOrDefaultAsync<TValue>(
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default);

    /// <summary>Checks on the server whether any row exists.</summary>
    Task<bool> AnyAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks on the server whether any row satisfies the predicate.</summary>
    Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether every row satisfies the predicate, by counting on the server the ones that
    /// <b>do not</b>.
    /// </summary>
    Task<bool> AllAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Equijoin with a query — <c>GENERATE</c>, <c>FILTER</c> and <c>SELECTCOLUMNS</c>. The inner
    /// side may come in <b>filtered</b>; only filters are accepted on it.
    /// </summary>
    /// <remarks>
    /// It redoes the join by the given key, not by the model's relationship. In a semantic model,
    /// grouping by a dimension column and summing a fact column already resolves the join through
    /// the relationships, with no <c>Join</c>. A composite key is written as <c>new { a, b }</c>.
    /// </remarks>
    DaxQuery<TResult> Join<TInner, TKey, TResult>(
        DaxQuery<TInner> inner,
        Expression<Func<T, TKey>> outerKeySelector,
        Expression<Func<TInner, TKey>> innerKeySelector,
        Expression<Func<T, TInner, TResult>> resultSelector)
        where TInner : class
        where TResult : class;

    /// <summary>Equijoin with another table, with no filter on the inner side.</summary>
    DaxQuery<TResult> Join<TInner, TKey, TResult>(
        IDaxTable<TInner> inner,
        Expression<Func<T, TKey>> outerKeySelector,
        Expression<Func<TInner, TKey>> innerKeySelector,
        Expression<Func<T, TInner, TResult>> resultSelector)
        where TInner : class
        where TResult : class;
}
