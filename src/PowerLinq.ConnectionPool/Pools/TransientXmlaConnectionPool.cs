using PowerLinq.ConnectionPool.Interfaces;

namespace PowerLinq.ConnectionPool.Pools;

/// <summary>
/// Implementation of <see cref="IXmlaConnectionPool"/> used when the pool is OFF
/// (<c>PowerBi:ConnectionPoolEnabled = false</c>). Reproduces the legacy behaviour (pre-ADR-024):
/// it opens a fresh XMLA connection on every rent and closes it on return — no reuse, no cap and
/// no eviction. It exists as a quick rollback in case the pool causes trouble in production.
/// </summary>
public sealed class TransientXmlaConnectionPool(IXmlaConnectionFactory factory) : IXmlaConnectionPool
{
    /// <inheritdoc/>
    public async Task<IPooledXmlaConnection> RentAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        IXmlaConnection connection = await factory.CreateOpenConnectionAsync(connectionString, connectionString, cancellationToken);
        return new TransientLease(connection);
    }

    // No pool: the connection is always closed on return (MarkBroken is irrelevant here).
    private sealed class TransientLease(IXmlaConnection connection) : IPooledXmlaConnection
    {
        private int _disposed;

        public IXmlaConnection Connection => connection;

        public void MarkBroken()
        {
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                connection.Dispose();
        }
    }
}
