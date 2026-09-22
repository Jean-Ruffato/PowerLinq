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
}
