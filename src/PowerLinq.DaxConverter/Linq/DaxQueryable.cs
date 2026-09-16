using System.Collections;
using System.Linq.Expressions;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.DaxConverter.Linq;

/// <summary>
/// A DAX query exposed as an <see cref="IQueryable{T}"/> — real LINQ over the same pipeline the
/// fluent API composes.
/// </summary>
/// <typeparam name="T">The entity, or the output contract after a projection.</typeparam>
/// <remarks>
/// <para>
/// <b>It is an additional surface, not a replacement.</b> The fluent API has a property this one
/// cannot have: there, what DAX does not express <b>does not compile</b>. Here
/// <c>.Where(x =&gt; Regex.IsMatch(x.Nome, p))</c> compiles and only fails during translation. What
/// you get in exchange is what only <see cref="IQueryable{T}"/> gives — query syntax, and being
/// able to hand the query to code that takes an <see cref="IQueryable{T}"/> without knowing this
/// library. The two coexist: replacing the fluent API with this surface was considered and
/// discarded.
/// </para>
/// <para>
/// <b>Translation happens during composition.</b> Each operator is translated into a
/// <see cref="DaxStage"/> when it is applied, not when the result is read: a <c>Where</c> that does
/// not translate blows up at the <c>Where</c>, with the stack pointing at the line that wrote it.
/// Holding the tree to translate at the terminal would give the right error in the wrong place.
/// </para>
/// <para>
/// It implements <see cref="IOrderedQueryable{T}"/> — and not just <see cref="IQueryable{T}"/> —
/// because that is what the compiler requires to accept <c>ThenBy</c> after <c>OrderBy</c>, and
/// for query syntax's <c>orderby ... , ...</c> clause. Ordering without having ordered before is
/// still refused, but for having no term to add, not because of the type.
/// </para>
/// </remarks>
public sealed class DaxQueryable<T> : IOrderedQueryable<T>, IDaxQueryable
{
    /// <summary>The root: the whole table, or an already-composed fluent query.</summary>
    internal DaxQueryable(DaxQueryProvider provider, DaxPipeline pipeline)
    {
        DaxProvider = provider;
        Pipeline = pipeline;
        Expression = Expression.Constant(this);
    }

    /// <summary>An operator applied over another query, with the pipeline already translated.</summary>
    internal DaxQueryable(DaxQueryProvider provider, DaxPipeline pipeline, Expression expression)
    {
        DaxProvider = provider;
        Pipeline = pipeline;
        Expression = expression;
    }

    /// <inheritdoc/>
    public Type ElementType => typeof(T);

    /// <inheritdoc/>
    public Expression Expression { get; }

    /// <inheritdoc/>
    public IQueryProvider Provider => DaxProvider;

    /// <inheritdoc/>
    public DaxPipeline Pipeline { get; }

    /// <inheritdoc/>
    public DaxQueryProvider DaxProvider { get; }

    /// <summary>
    /// <b>Always throws.</b> There is no synchronous enumeration: use <c>ToListAsync</c> or
    /// <c>AsAsyncEnumerable</c>.
    /// </summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    /// <remarks>
    /// <para>
    /// The query goes to an XMLA endpoint, and the executor is asynchronous end to end. Serving a
    /// <c>foreach</c> would require blocking the thread while waiting on the network — <i>sync over
    /// async</i>, which consumes a pool thread per in-flight query and is the classic way to starve
    /// the pool under load. The symptom shows up far from the cause, which is exactly the kind of
    /// silent trap this library refuses instead of degrading into.
    /// </para>
    /// <para>
    /// <b>It is not client-side evaluation.</b> Nothing here translates part of the query and runs
    /// the rest in memory: the asynchronous terminals execute everything on the server. The
    /// boundary for continuing in memory is explicit and has a name — <c>ToListAsync</c>
    /// materializes, <c>AsAsyncEnumerable</c> hands back row by row — and LINQ to Objects applies
    /// after it.
    /// </para>
    /// </remarks>
    public IEnumerator<T> GetEnumerator() =>
        throw new NotSupportedException(DaxProvider.Localizer.Get("QueryableSyncEnumeration"));

    /// <inheritdoc cref="GetEnumerator"/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
