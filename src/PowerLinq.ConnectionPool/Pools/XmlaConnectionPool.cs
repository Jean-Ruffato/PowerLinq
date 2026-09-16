using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PowerLinq.ConnectionPool.Diagnostics;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.ConnectionPool.Options;

namespace PowerLinq.ConnectionPool.Pools;

/// <summary>
/// Singleton pool of XMLA connections (ADR-024). Per key (endpoint + catalog) it keeps a bounded
/// set (<see cref="XmlaConnectionPoolOptions.MaxConnectionsPerModel"/>) of reusable open connections.
///
/// Concurrency correctness: ADOMD is not thread-safe, so each connection serves one command at a
/// time. A <see cref="SemaphoreSlim"/> per key bounds the simultaneous leases (and therefore the
/// total of physical connections per key); idle connections live on a lock-protected stack and get
/// reused. The operations on that stack (rent, return and eviction) are serialized by the same
/// lock, so eviction never opens a window in which a concurrent rent sees an empty stack and
/// creates connections beyond the cap. Connections idle for longer than
/// <see cref="XmlaConnectionPoolOptions.ConnectionIdleTimeoutMinutes"/> are closed by a timer.
/// </summary>
public sealed class XmlaConnectionPool : IXmlaConnectionPool, IDisposable
{
    private readonly IXmlaConnectionFactory _factory;
    private readonly ILogger<XmlaConnectionPool> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxPerKey;
    private readonly TimeSpan _idleTimeout;
    private readonly ConcurrentDictionary<string, PoolSlot> _slots = new(StringComparer.Ordinal);
    private readonly ITimer _evictionTimer;
    private volatile bool _disposed;

    /// <param name="factory">Opens XMLA connections; the seam that isolates ADOMD.NET.</param>
    /// <param name="options">Per-key cap and idle timeout.</param>
    /// <param name="timeProvider">
    /// Source of time for idle expiry and for the eviction timer. Registered as
    /// <see cref="TimeProvider.System"/> by the DI extension; in tests, a controllable
    /// implementation makes eviction verifiable without waiting in real time.
    /// </param>
    /// <param name="logger">Logs connection opening and failures while closing.</param>
    public XmlaConnectionPool(
        IXmlaConnectionFactory factory,
        XmlaConnectionPoolOptions options,
        TimeProvider timeProvider,
        ILogger<XmlaConnectionPool> logger)
    {
        _factory = factory;
        _logger = logger;
        _timeProvider = timeProvider;
        _maxPerKey = Math.Max(1, options.MaxConnectionsPerModel);
        _idleTimeout = TimeSpan.FromMinutes(Math.Max(1, options.ConnectionIdleTimeoutMinutes));

        var period = TimeSpan.FromMinutes(1);
        _evictionTimer = _timeProvider.CreateTimer(_ => EvictIdle(), null, period, period);

        // Observable gauge instead of a counter: live and idle connections are state, and
        // rebuilding state by summing increments goes wrong on any error path that loses a
        // decrement.
        PowerLinqDiagnostics.RegisterPoolGauges(ReadState);
    }

    /// <summary>
    /// The same dimensions the executor uses — workspace and dataset — decomposed from the
    /// connection string. Labelling the pool with the whole string would force the dashboard to
    /// join against the query metrics just to talk about the same model.
    /// </summary>
    private static KeyValuePair<string, object?>[] KeyTag(string connectionString) =>
        PowerLinqDiagnostics.TargetTags(connectionString);

    private static KeyValuePair<string, object?>[] Origin(string connectionString, string origin) =>
        [.. PowerLinqDiagnostics.TargetTags(connectionString), new("powerlinq.pool.origin", origin)];

    /// <summary>Current pool state, for the observable gauges.</summary>
    private (int Live, int Idle) ReadState()
    {
        int live = 0;
        int idle = 0;

        foreach (PoolSlot slot in _slots.Values)
        {
            (int slotLive, int slotIdle) = slot.Count();
            live += slotLive;
            idle += slotIdle;
        }

        return (live, idle);
    }

    /// <inheritdoc/>
    public async Task<IPooledXmlaConnection> RentAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ObjectDisposedException.ThrowIf(_disposed, this);

        PoolSlot slot = _slots.GetOrAdd(connectionString, _ => new PoolSlot(_maxPerKey));

        // The wait on the semaphore is measured in its own right: with headroom it stays near
        // zero, and when the per-model cap starts to bite it grows BEFORE any error shows up.
        // Without this metric, MaxConnectionsPerModel saturation only manifests as unexplained
        // query latency.
        long waitStart = Stopwatch.GetTimestamp();

        // Bounds simultaneous leases per key => bounds the physical connections per key.
        await slot.Gate.WaitAsync(cancellationToken);

        PowerLinqDiagnostics.PoolWait.Record(
            Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds, KeyTag(connectionString));

        try
        {
            PooledConnection? reused = slot.TakeIdle(_timeProvider.GetUtcNow(), _idleTimeout, SafeDispose);
            if (reused is not null)
            {
                PowerLinqDiagnostics.PoolRentals.Add(1, Origin(connectionString, "reused"));
                return new Lease(this, slot, reused);
            }

            IXmlaConnection connection = await _factory.CreateOpenConnectionAsync(
                connectionString,
                connectionString,
                cancellationToken);

            _logger.LogDebug("Conexão XMLA aberta para {Key}.", connectionString);
            PowerLinqDiagnostics.PoolRentals.Add(1, Origin(connectionString, "opened"));
            return new Lease(this, slot, new PooledConnection(connection, _timeProvider.GetUtcNow()));
        }
        catch
        {
            // Failed before handing out the lease: release the slot so the semaphore does not leak.
            slot.Gate.Release();
            throw;
        }
    }

    // Returns the connection to the pool (or discards it if broken/shutting down), freeing the key's slot.
    private void Return(PoolSlot slot, PooledConnection pooled, bool broken)
    {
        if (broken || _disposed)
            SafeDispose(pooled.Connection);
        else
            slot.ReturnIdle(pooled, _timeProvider.GetUtcNow());

        // The Gate is never disposed (we do not use AvailableWaitHandle), so Release is always
        // safe, even for an in-flight lease that returns after the pool's Dispose.
        slot.Gate.Release();
    }

    private void EvictIdle()
    {
        if (_disposed)
            return;

        DateTimeOffset now = _timeProvider.GetUtcNow();

        foreach (PoolSlot slot in _slots.Values)
            slot.EvictExpired(now, _idleTimeout, SafeDispose);
    }

    /// <summary>Closes the eviction timer and every live connection. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _evictionTimer.Dispose();

        // Closes the idle connections. In-flight leases close/return their own in their Dispose
        // (Return sees _disposed == true and discards). The SemaphoreSlim instances are NOT
        // disposed on purpose: we do not use AvailableWaitHandle, so there is no resource to
        // release, and a Release/WaitAsync from a rent racing with shutdown therefore never
        // throws ObjectDisposedException.
        foreach (PoolSlot slot in _slots.Values)
            slot.DisposeIdle(SafeDispose);

        _slots.Clear();
    }

    private void SafeDispose(IXmlaConnection connection)
    {
        try
        {
            connection.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao fechar conexão XMLA (ignorado).");
        }
    }

    private sealed class PoolSlot(int maxPerKey)
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

    private sealed class PooledConnection(IXmlaConnection connection, DateTimeOffset createdUtc)
    {
        // Only touched under the PoolSlot lock. The instant comes from outside, from the pool's
        // TimeProvider, so expiry is verifiable without waiting in real time.
        private DateTimeOffset _lastUsedUtc = createdUtc;

        public IXmlaConnection Connection { get; } = connection;

        public void Touch(DateTimeOffset now) => _lastUsedUtc = now;

        public bool IsExpired(DateTimeOffset now, TimeSpan idleTimeout) =>
            now - _lastUsedUtc > idleTimeout;
    }

    private sealed class Lease(XmlaConnectionPool pool, PoolSlot slot, PooledConnection pooled)
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
}
