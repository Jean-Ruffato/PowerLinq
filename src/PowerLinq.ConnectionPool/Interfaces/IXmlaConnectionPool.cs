namespace PowerLinq.ConnectionPool.Interfaces;

/// <summary>
/// Singleton pool of reusable XMLA connections, keyed by connection string
/// (endpoint + catalog). Removes the per-query connection open (ADR-024).
/// </summary>
public interface IXmlaConnectionPool
{
    /// <summary>
    /// Rents an open connection for the key. Reuses an idle one or opens a new one
    /// (up to <c>MaxConnectionsPerModel</c>). Always return it through the lease's
    /// <see cref="IDisposable.Dispose"/>; call <see cref="IPooledXmlaConnection.MarkBroken"/> if
    /// the command signals a dead session, so the connection is discarded instead of reused.
    /// </summary>
    Task<IPooledXmlaConnection> RentAsync(string connectionString, CancellationToken cancellationToken = default);
}
