using PowerLinq.ConnectionPool.Interfaces;

namespace PowerLinq.ConnectionPool.Pools;

internal sealed class PooledConnection(IXmlaConnection connection, DateTimeOffset createdUtc)
{
    // Only touched under the PoolSlot lock. The instant comes from outside, from the pool's
    // TimeProvider, so expiry is verifiable without waiting in real time.
    private DateTimeOffset _lastUsedUtc = createdUtc;

    public IXmlaConnection Connection { get; } = connection;

    public void Touch(DateTimeOffset now) => _lastUsedUtc = now;

    public bool IsExpired(DateTimeOffset now, TimeSpan idleTimeout) =>
        now - _lastUsedUtc > idleTimeout;
}
