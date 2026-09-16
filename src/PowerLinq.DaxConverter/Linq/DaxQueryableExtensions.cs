using System.Linq.Expressions;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.DaxConverter.Linq;

/// <summary>
/// The bridge between the two surfaces, and the execution terminals of <see cref="IQueryable{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The terminals are asynchronous because execution is remote.</b> Standard LINQ ends in
/// <c>ToList()</c> and <c>Count()</c>, which are synchronous by definition of the interface; here
/// each of them is a trip to the XMLA endpoint. That is why execution lives in methods of its own —
/// it is EF Core's design, and for the same reason.
/// </para>
/// <para>
/// None of them reimplements anything: they all convert the query back into a
/// <see cref="DaxQuery{T}"/> and call the terminal that already existed. The two surfaces share
/// composition <b>and</b> execution — which is what "the fluent API becomes a layer over the same
/// engine" means.
/// </para>
/// </remarks>
public static class DaxQueryableExtensions
{
    /// <summary>The table as an <see cref="IQueryable{T}"/>, to compose with real LINQ.</summary>
    /// <typeparam name="T">The entity.</typeparam>
    /// <param name="table">The table, coming from <c>DaxContext.Set</c>.</param>
    /// <exception cref="NotSupportedException">
    /// The table did not come from <c>DaxTableFactory</c> — without the concrete implementation
    /// there is no executor to attach the query to.
    /// </exception>
    /// <remarks>
    /// An extension method, not a member of <see cref="IDaxTable{T}"/>: adding a member to a
    /// published interface forces every existing implementation to change, and there are 29 in the
    /// solution, nearly all of them test fakes. It is the same reason <c>IDaxRawQueryExecutor</c>
    /// is a separate interface.
    /// </remarks>
    public static IQueryable<T> AsQueryable<T>(this IDaxTable<T> table) where T : class
    {
        ArgumentNullException.ThrowIfNull(table);

        if (table is not DaxTable<T> concrete)
            throw new NotSupportedException(ResourceManagerPowerLinqLocalizer.English.Get("QueryableFactoryRequired"));

        return new DaxQueryProvider(concrete.Executor, concrete.Localizer).Root<T>();
    }

    /// <summary>
    /// An already-composed fluent query as an <see cref="IQueryable{T}"/>.
    /// </summary>
    /// <typeparam name="T">The entity, or the output contract of a projection.</typeparam>
    /// <param name="query">The query.</param>
    /// <remarks>
    /// This is the interoperability path that motivated the surface: filter with the API that fails
    /// at compile time and <b>then</b> hand the query to code that only knows
    /// <see cref="IQueryable{T}"/> — generic pagination, dynamic filtering, OData. What crosses over
    /// is the pipeline, so nothing from the earlier composition is lost.
    /// </remarks>
    public static IQueryable<T> AsQueryable<T>(this DaxQuery<T> query) where T : class
    {
        ArgumentNullException.ThrowIfNull(query);

        return new DaxQueryable<T>(
            new DaxQueryProvider(query.Executor, query.Localizer), query.Pipeline);
    }

    /// <summary>The DAX the query would send to the server, without running it.</summary>
    /// <typeparam name="T">The query's element.</typeparam>
    /// <param name="source">The query.</param>
    public static string ToDaxString<T>(this IQueryable<T> source) where T : class =>
        Query(source).ToDaxString();

    /// <summary>Runs the query and materializes every row.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<List<T>> ToListAsync<T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).ToListAsync(cancellationToken);

    /// <summary>Runs the query and materializes every row as an array.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<T[]> ToArrayAsync<T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).ToArrayAsync(cancellationToken);

    /// <summary>Materializes every row indexed by the key. The selector runs on the client.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <typeparam name="TKey">The key's type.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="keySelector">The key, evaluated over the already-materialized rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<Dictionary<TKey, T>> ToDictionaryAsync<T, TKey>(
        this IQueryable<T> source,
        Func<T, TKey> keySelector,
        CancellationToken cancellationToken = default)
        where T : class where TKey : notnull =>
        Query(source).ToDictionaryAsync(keySelector, cancellationToken);

    /// <summary>
    /// The rows as they arrive, without materializing the list. It requires an executor that also
    /// implements <see cref="IDaxStreamingQueryExecutor"/>.
    /// </summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// It is the explicit boundary for continuing in memory: what comes back is a sequence, and
    /// from there on filtering and ordering happen on the client. Having its own name is the point
    /// — nothing falls into client-side evaluation by omission.
    /// </remarks>
    public static IAsyncEnumerable<T> AsAsyncEnumerable<T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).AsAsyncEnumerable(cancellationToken);

    /// <summary>
    /// The page <b>and</b> the total of the whole set, in a single query. It requires an executor
    /// that also implements <see cref="IDaxRawQueryExecutor"/>.
    /// </summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query, already carrying the page's <c>Skip</c> and <c>Take</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<DaxPage<T>> ToPagedListAsync<T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).ToPagedListAsync(cancellationToken);

    /// <summary>First row, or <see langword="null"/> when there is none.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<T?> FirstOrDefaultAsync<T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).FirstOrDefaultAsync(cancellationToken);

    /// <summary>First row that satisfies the predicate, or <see langword="null"/>.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="predicate">The filter, translated and applied on the server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<T?> FirstOrDefaultAsync<T>(
        this IQueryable<T> source,
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).Where(predicate).FirstOrDefaultAsync(cancellationToken);

    /// <summary>First row; throws when the result is empty.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<T> FirstAsync<T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).FirstAsync(cancellationToken);

    /// <summary>First row that satisfies the predicate; throws when there is none.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="predicate">The filter, translated and applied on the server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<T> FirstAsync<T>(
        this IQueryable<T> source,
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).Where(predicate).FirstAsync(cancellationToken);

    /// <summary>The single row of the result; throws when there are zero or more than one.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<T> SingleAsync<T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).SingleAsync(cancellationToken);

    /// <summary>
    /// The single row of the result, or <see langword="null"/> when there is none; it still throws
    /// when there is more than one.
    /// </summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<T?> SingleOrDefaultAsync<T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).SingleOrDefaultAsync(cancellationToken);

    /// <summary>Counts the rows on the server — <c>COUNTROWS</c>, without fetching them.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<int> CountAsync<T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).CountAsync(cancellationToken);

    /// <summary>Counts on the server the rows that satisfy the predicate.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="predicate">The filter, translated and applied on the server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<int> CountAsync<T>(
        this IQueryable<T> source,
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).Where(predicate).CountAsync(cancellationToken);

    /// <summary>Counts the rows on the server, returning a <see cref="long"/>.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<long> LongCountAsync<T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).LongCountAsync(cancellationToken);

    /// <summary>Checks on the server whether any row exists.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<bool> AnyAsync<T>(
        this IQueryable<T> source,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).AnyAsync(cancellationToken);

    /// <summary>Checks on the server whether any row satisfies the predicate.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="predicate">The filter, translated and applied on the server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<bool> AnyAsync<T>(
        this IQueryable<T> source,
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).AnyAsync(predicate, cancellationToken);

    /// <summary>Checks whether every row satisfies the predicate.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="predicate">The filter, translated and applied on the server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<bool> AllAsync<T>(
        this IQueryable<T> source,
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).AllAsync(predicate, cancellationToken);

    /// <summary>Sums the column on the server — <c>SUMX</c>.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <typeparam name="TValue">The column's type.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="selector">The column to sum.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<TValue> SumAsync<T, TValue>(
        this IQueryable<T> source,
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).SumAsync(selector, cancellationToken);

    /// <summary>Smallest value of the column on the server — <c>MINX</c>.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <typeparam name="TValue">The column's type.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="selector">The column.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<TValue> MinAsync<T, TValue>(
        this IQueryable<T> source,
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).MinAsync(selector, cancellationToken);

    /// <summary>Largest value of the column on the server — <c>MAXX</c>.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <typeparam name="TValue">The column's type.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="selector">The column.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<TValue> MaxAsync<T, TValue>(
        this IQueryable<T> source,
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).MaxAsync(selector, cancellationToken);

    /// <summary>
    /// Average of the column on the server — <c>AVERAGEX</c>. It returns a <see cref="double"/>
    /// rather than the column's type, because the average of an integer column is not an integer.
    /// </summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <typeparam name="TValue">The column's type.</typeparam>
    /// <param name="source">The query.</param>
    /// <param name="selector">The column.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<double> AverageAsync<T, TValue>(
        this IQueryable<T> source,
        Expression<Func<T, TValue>> selector,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).AverageAsync(selector, cancellationToken);

    /// <summary>
    /// Reads a <b>model measure</b>, with the composed filters entering as filter context.
    /// </summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <typeparam name="TValue">The result's type.</typeparam>
    /// <param name="source">The query, whose filters become the measure's slice.</param>
    /// <param name="measure">The measure's name, with or without brackets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<TValue?> MeasureAsync<T, TValue>(
        this IQueryable<T> source,
        string measure,
        CancellationToken cancellationToken = default) where T : class =>
        Query(source).MeasureAsync<TValue>(measure, cancellationToken);

    /// <summary>
    /// The fluent query behind the <see cref="IQueryable{T}"/> — the same pipeline, on the surface
    /// that carries the terminals.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The <see cref="IQueryable{T}"/> did not come from this provider. One of this library's
    /// terminals over another provider's query has nothing to execute, and the message says so
    /// instead of the error showing up as an invalid cast.
    /// </exception>
    private static DaxQuery<T> Query<T>(IQueryable<T> source) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source is not DaxQueryable<T> queryable)
        {
            throw new NotSupportedException(
                ResourceManagerPowerLinqLocalizer.English.Get("QueryableForeignSource"));
        }

        return new DaxQuery<T>(
            queryable.DaxProvider.Executor, queryable.Pipeline, queryable.DaxProvider.Localizer);
    }
}
