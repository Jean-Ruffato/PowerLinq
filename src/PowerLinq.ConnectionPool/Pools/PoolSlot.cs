using PowerLinq.ConnectionPool.Interfaces;

namespace PowerLinq.ConnectionPool.Pools;

internal sealed class PoolSlot(int maxPerKey)
{
    private readonly object _sync = new();
    private readonly Stack<PooledConnection> _idle = new();

    public SemaphoreSlim Gate { get; } = new(maxPerKey, maxPerKey);

    /// <summary>
    /// Live and idle connections in this slot, for the state gauges.
    /// </summary>
    /// <remarks>
    /// Live = idle + lent, and lent is what the semaphore is missing. Deriving it from the
    /// semaphore avoids a parallel counter, which would drift from the real state on any
    /// error path.
    /// </remarks>
    public (int Live, int Idle) Count()
    {
        lock (_sync)
        {
            int lent = maxPerKey - Gate.CurrentCount;
            return (_idle.Count + lent, _idle.Count);
        }
    }

    // Takes a valid idle connection (discarding expired ones). The expiry check runs under the
    // lock; the Dispose of the expired ones runs outside it.
    public PooledConnection? TakeIdle(
        DateTimeOffset now,
        TimeSpan idleTimeout,
        Action<IXmlaConnection> dispose)
    {
        List<IXmlaConnection>? expired = null;
        PooledConnection? result = null;

        lock (_sync)
        {
            while (_idle.Count > 0)
            {
                PooledConnection candidate = _idle.Pop();
                if (candidate.IsExpired(now, idleTimeout))
                {
                    (expired ??= []).Add(candidate.Connection);
                    continue;
                }

                result = candidate;
                break;
            }
        }

        if (expired is not null)
        {
            foreach (IXmlaConnection connection in expired)
            {
                dispose(connection);
            }
        }

        return result;
    }

    public void ReturnIdle(PooledConnection pooled, DateTimeOffset now)
    {
        lock (_sync)
        {
            pooled.Touch(now);
            _idle.Push(pooled);
        }
    }

    public void EvictExpired(
        DateTimeOffset now,
        TimeSpan idleTimeout,
        Action<IXmlaConnection> dispose)
    {
        List<IXmlaConnection>? expired = null;

        lock (_sync)
        {
            if (_idle.Count == 0)
                return;

            var survivors = new Stack<PooledConnection>(_idle.Count);
            while (_idle.Count > 0)
            {
                PooledConnection pooled = _idle.Pop();
                if (pooled.IsExpired(now, idleTimeout))
                    (expired ??= []).Add(pooled.Connection);
                else
                    survivors.Push(pooled);
            }

            while (survivors.Count > 0)
                _idle.Push(survivors.Pop());
        }

        if (expired is not null)
        {
            foreach (IXmlaConnection connection in expired)
            {
                dispose(connection);
            }
        }
    }

    public void DisposeIdle(Action<IXmlaConnection> dispose)
    {
        List<IXmlaConnection> all = [];
        lock (_sync)
        {
            while (_idle.Count > 0)
                all.Add(_idle.Pop().Connection);
        }

        foreach (IXmlaConnection connection in all)
            dispose(connection);
    }
}
