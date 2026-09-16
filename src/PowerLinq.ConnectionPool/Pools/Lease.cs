using PowerLinq.ConnectionPool.Interfaces;

namespace PowerLinq.ConnectionPool.Pools;

internal sealed class Lease(XmlaConnectionPool pool, PoolSlot slot, PooledConnection pooled)
    : IPooledXmlaConnection
{
    private int _broken;
    private int _returned;

    public IXmlaConnection Connection => pooled.Connection;

    public void MarkBroken() => Interlocked.Exchange(ref _broken, 1);

    public void Dispose()
    {
        // Idempotent and thread-safe: it returns exactly once.
        if (Interlocked.Exchange(ref _returned, 1) != 0)
            return;

        pool.Return(slot, pooled, Volatile.Read(ref _broken) == 1);
    }
}
