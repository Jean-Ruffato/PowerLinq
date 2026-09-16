namespace PowerLinq.DaxConverter.Interfaces;

/// <summary>
/// An executor that also delivers the result <b>row by row</b>, without materializing the list.
/// </summary>
/// <remarks>
/// <para>
/// A separate interface rather than a new member on <see cref="IDaxQueryExecutor"/>: there are 29
/// implementations in the solution, nearly all of them test fakes, and forcing them to gain a
/// method they do not use would cost more than it solves. It is the same pattern as
/// <see cref="IDaxRawQueryExecutor"/>.
/// </para>
/// <para>
/// <b>Alongside <c>ExecuteAsync</c>, not in its place.</b> Whoever needs the list keeps asking for
/// the list — most screens do, because they sort, count or serialize the whole set — and nobody
/// pays the cost of rewriting for a shape they do not use.
/// </para>
/// </remarks>
public interface IDaxStreamingQueryExecutor
{
    /// <summary>Runs the query and returns the rows as they arrive.</summary>
    /// <typeparam name="T">The contract of each row.</typeparam>
    /// <param name="daxQuery">The query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Abandoning the enumeration partway — a <c>break</c>, a <c>return</c> or an exception in the
    /// loop — has to release what the read was holding. That is what <c>await foreach</c>
    /// guarantees by calling the enumerator's <c>DisposeAsync</c>, and it is why the signature
    /// returns <see cref="IAsyncEnumerable{T}"/> instead of a <c>Task</c> of a sequence.
    /// </remarks>
    IAsyncEnumerable<T> ExecuteStreamAsync<T>(
        string daxQuery,
        CancellationToken cancellationToken = default) where T : class;
}
