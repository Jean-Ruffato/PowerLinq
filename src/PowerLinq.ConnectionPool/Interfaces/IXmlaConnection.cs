using PowerLinq.DaxConverter.Execution;

namespace PowerLinq.ConnectionPool.Interfaces;

/// <summary>
/// Abstraction over an already-open, reusable XMLA connection. Wraps ADOMD.NET
/// (<c>AdomdConnection</c> + command + reader) so the pool can be tested with fakes.
/// NOT thread-safe — it must serve one command at a time (which the pool guarantees).
/// </summary>
public interface IXmlaConnection : IDisposable
{
    /// <summary>Pool key (endpoint + catalog) this connection belongs to.</summary>
    string Key { get; }

    /// <summary>Runs the DAX query and materializes the result. Throws on session/transport failure.</summary>
    DaxResult ExecuteQuery(string daxQuery, int commandTimeoutSeconds, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the DAX query and returns the rows <b>as they arrive</b>, without materializing the
    /// set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default implementation does <b>not</b> stream: it runs through
    /// <see cref="ExecuteQuery"/> and walks the finished result. The rows are the same and the
    /// order is the same — what it does not deliver is the memory saving, because the set was
    /// already materialized before the first row came out.
    /// </para>
    /// <para>
    /// It exists this way so that the eleven implementations of this interface — nearly all of
    /// them test fakes — did not have to gain a method they do not use.
    /// <c>AdomdXmlaConnection</c> overrides it with the real streaming read, and that is the one
    /// that runs in production.
    /// </para>
    /// </remarks>
    IEnumerable<DaxRow> StreamQuery(
        string daxQuery,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken) =>
        ExecuteQuery(daxQuery, commandTimeoutSeconds, cancellationToken).Rows;
}
