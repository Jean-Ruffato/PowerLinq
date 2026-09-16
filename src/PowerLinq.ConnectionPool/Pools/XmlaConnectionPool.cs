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
    internal void Return(PoolSlot slot, PooledConnection pooled, bool broken)
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
}
