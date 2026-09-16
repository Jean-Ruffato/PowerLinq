using PowerLinq.ConnectionPool.Interfaces;

namespace PowerLinq.ConnectionPool.Pools;

// No pool: the connection is always closed on return (MarkBroken is irrelevant here).
internal sealed class TransientLease(IXmlaConnection connection) : IPooledXmlaConnection
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
