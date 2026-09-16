using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using PowerLinq.ConnectionPool.Diagnostics;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.ConnectionPool.Options;
using PowerLinq.ConnectionPool.Pools;

namespace PowerLinq.Tests.ConnectionPool;

/// <summary>
/// <see cref="XmlaConnectionPool"/>'s <c>summary</c> describes concurrency invariants that until
/// now existed only as a comment: the per-key connection cap, and eviction serialized with
/// renting so that it "never opens a window in which a concurrent rent sees an empty stack and
/// creates connections beyond the cap". These tests verify them.
/// </summary>
public sealed class XmlaConnectionPoolTests
{
    private const string KeyA = "Data Source=powerbi://a;Catalog=Modelo;";
    private const string KeyB = "Data Source=powerbi://b;Catalog=Outro;";

    private static readonly DateTimeOffset Start = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    private static (XmlaConnectionPool Pool, FakeXmlaConnectionFactory Factory, ControllableTimeProvider Clock)
        Create(int maxPerKey = 2, int idleMinutes = 15)
    {
        var factory = new FakeXmlaConnectionFactory();
        var clock = new ControllableTimeProvider(Start);
        var pool = new XmlaConnectionPool(
            factory,
            new XmlaConnectionPoolOptions(maxPerKey, idleMinutes),
            clock,
            NullLogger<XmlaConnectionPool>.Instance);

        return (pool, factory, clock);
    }

    // ---------- per-key cap ----------

    [Fact]
    public async Task ConcurrentRents_NeverExceedMaxPerKey()
    {
        (XmlaConnectionPool pool, FakeXmlaConnectionFactory factory, _) = Create(maxPerKey: 2);
        using var release = new SemaphoreSlim(0);
        var holding = 0;
        var peak = 0;

        // 8 tasks contend over 2 slots. Each holds its lease until the test releases them, so the
        // peak of simultaneous leases is the semaphore's real cap.
        Task[] workers = [.. Enumerable.Range(0, 8).Select(async _ =>
        {
            using IPooledXmlaConnection lease = await pool.RentAsync(KeyA);
            int current = Interlocked.Increment(ref holding);
            InterlockedMax(ref peak, current);
            await release.WaitAsync();
            Interlocked.Decrement(ref holding);
        })];

        // Gives the 8 tasks time to reach RentAsync before releasing.
        while (Volatile.Read(ref holding) < 2)
            await Task.Yield();

        release.Release(8);
        await Task.WhenAll(workers);

        Assert.Equal(2, Volatile.Read(ref peak));
        Assert.True(factory.CreatedCount <= 2, $"abriu {factory.CreatedCount} conexões para um teto de 2");

        pool.Dispose();
    }

    [Fact]
    public async Task MaxPerKey_IsPerKeyNotGlobal()
    {
        (XmlaConnectionPool pool, FakeXmlaConnectionFactory factory, _) = Create(maxPerKey: 1);

        using IPooledXmlaConnection first = await pool.RentAsync(KeyA);
        using IPooledXmlaConnection second = await pool.RentAsync(KeyB);

        // Different keys do not compete with each other.
        Assert.Equal(2, factory.CreatedCount);
        Assert.NotSame(first.Connection, second.Connection);

        pool.Dispose();
    }

    [Fact]
    public async Task MaxPerKeyBelowOne_IsClampedToOne()
    {
        (XmlaConnectionPool pool, FakeXmlaConnectionFactory factory, _) = Create(maxPerKey: 0);

        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
            Assert.Equal(1, factory.CreatedCount);
        }

        using (IPooledXmlaConnection reused = await pool.RentAsync(KeyA))
        {
            Assert.Equal(1, factory.CreatedCount);
        }

        pool.Dispose();
    }

    // ---------- reuse ----------

    [Fact]
    public async Task ReturnedConnection_IsReusedInsteadOfOpeningAnother()
    {
        (XmlaConnectionPool pool, FakeXmlaConnectionFactory factory, _) = Create();

        IXmlaConnection firstConnection;
        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
            firstConnection = lease.Connection;
        }

        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
            Assert.Same(firstConnection, lease.Connection);
        }

        Assert.Equal(1, factory.CreatedCount);
        pool.Dispose();
    }

    [Fact]
    public async Task BrokenConnection_IsDisposedAndNotReused()
    {
        (XmlaConnectionPool pool, FakeXmlaConnectionFactory factory, _) = Create();

        FakeXmlaConnection first;
        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
            first = (FakeXmlaConnection)lease.Connection;
            lease.MarkBroken();
        }

        Assert.True(first.IsDisposed);

        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
            Assert.NotSame(first, lease.Connection);
        }

        Assert.Equal(2, factory.CreatedCount);
        pool.Dispose();
    }

    [Fact]
    public async Task LeaseDispose_IsIdempotent()
    {
        (XmlaConnectionPool pool, FakeXmlaConnectionFactory factory, _) = Create(maxPerKey: 1);

        IPooledXmlaConnection lease = await pool.RentAsync(KeyA);
        lease.Dispose();
        lease.Dispose();
        lease.Dispose();

        // If Dispose released the semaphore three times, the cap of 1 would stop holding and two
        // simultaneous leases would get through.
        using IPooledXmlaConnection held = await pool.RentAsync(KeyA);
        Task<IPooledXmlaConnection> blocked = pool.RentAsync(KeyA);

        Assert.False(blocked.IsCompleted, "o teto por chave foi violado por Dispose repetido");

        held.Dispose();
        (await blocked).Dispose();
        pool.Dispose();
    }

    // ---------- expiry and eviction ----------

    [Fact]
    public async Task IdleConnectionPastTimeout_IsNotReused()
    {
        (XmlaConnectionPool pool, FakeXmlaConnectionFactory factory, ControllableTimeProvider clock) =
            Create(idleMinutes: 15);

        FakeXmlaConnection first;
        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
            first = (FakeXmlaConnection)lease.Connection;
        }

        clock.Advance(TimeSpan.FromMinutes(16));

        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
            Assert.NotSame(first, lease.Connection);
        }

        Assert.True(first.IsDisposed, "a conexão expirada deveria ter sido fechada ao ser descartada da pilha");
        Assert.Equal(2, factory.CreatedCount);
        pool.Dispose();
    }

    [Fact]
    public async Task IdleConnectionWithinTimeout_IsStillReused()
    {
        (XmlaConnectionPool pool, FakeXmlaConnectionFactory factory, ControllableTimeProvider clock) =
            Create(idleMinutes: 15);

        FakeXmlaConnection first;
        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
            first = (FakeXmlaConnection)lease.Connection;
        }

        clock.Advance(TimeSpan.FromMinutes(14));

        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
            Assert.Same(first, lease.Connection);
        }

        Assert.False(first.IsDisposed);
        Assert.Equal(1, factory.CreatedCount);
        pool.Dispose();
    }

    [Fact]
    public async Task EvictionTimer_ClosesIdleConnectionWithoutAnyRent()
    {
        (XmlaConnectionPool pool, _, ControllableTimeProvider clock) = Create(idleMinutes: 5);

        FakeXmlaConnection first;
        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
            first = (FakeXmlaConnection)lease.Connection;
        }

        // The timer runs every minute; passing the idle timeout triggers the sweep without anyone
        // asking for a new connection.
        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.True(first.IsDisposed);
        pool.Dispose();
    }

    [Fact]
    public async Task Eviction_KeepsConnectionsThatAreStillFresh()
    {
        (XmlaConnectionPool pool, _, ControllableTimeProvider clock) = Create(maxPerKey: 2, idleMinutes: 10);

        IPooledXmlaConnection a = await pool.RentAsync(KeyA);
        IPooledXmlaConnection b = await pool.RentAsync(KeyA);
        var older = (FakeXmlaConnection)a.Connection;
        var newer = (FakeXmlaConnection)b.Connection;

        a.Dispose();
        clock.Advance(TimeSpan.FromMinutes(8));
        b.Dispose();

        // 4 more minutes: the first accumulates 12 (expired), the second only 4.
        clock.Advance(TimeSpan.FromMinutes(4));

        Assert.True(older.IsDisposed);
        Assert.False(newer.IsDisposed);

        using IPooledXmlaConnection reused = await pool.RentAsync(KeyA);
        Assert.Same(newer, reused.Connection);

        pool.Dispose();
    }

    [Fact]
    public async Task EvictionConcurrentWithRent_NeverExceedsMaxPerKey()
    {
        (XmlaConnectionPool pool, FakeXmlaConnectionFactory factory, ControllableTimeProvider clock) =
            Create(maxPerKey: 2, idleMinutes: 1);

        // Seeds two idle ones.
        IPooledXmlaConnection a = await pool.RentAsync(KeyA);
        IPooledXmlaConnection b = await pool.RentAsync(KeyA);
        a.Dispose();
        b.Dispose();

        // Sweeping and renting at the same time, repeatedly: it is the window the class's summary
        // says does not exist.
        for (int round = 0; round < 40; round++)
        {
            Task sweep = Task.Run(() => clock.Advance(TimeSpan.FromMinutes(2)));
            Task rents = Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            {
                using IPooledXmlaConnection lease = await pool.RentAsync(KeyA);
                await Task.Yield();
            }));

            await Task.WhenAll(sweep, rents);
        }

        // The cap still holds after all the churn.
        using IPooledXmlaConnection first = await pool.RentAsync(KeyA);
        using IPooledXmlaConnection second = await pool.RentAsync(KeyA);
        Task<IPooledXmlaConnection> third = pool.RentAsync(KeyA);

        Assert.False(third.IsCompleted, "o teto por chave foi violado sob eviction concorrente");

        first.Dispose();
        (await third).Dispose();
        pool.Dispose();
    }

    // ---------- failures and shutdown ----------

    [Fact]
    public async Task FailureWhileOpening_ReleasesTheSlot()
    {
        var factory = new ThrowingXmlaConnectionFactory(new InvalidOperationException("sem rede"));
        var pool = new XmlaConnectionPool(
            factory,
            new XmlaConnectionPoolOptions(1, 15),
            new ControllableTimeProvider(Start),
            NullLogger<XmlaConnectionPool>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.RentAsync(KeyA));

        // If the slot leaked, this second attempt would hang on the semaphore instead of reaching
        // the factory and failing again.
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.RentAsync(KeyA));

        Assert.Equal(2, factory.Attempts);
        pool.Dispose();
    }

    [Fact]
    public async Task Dispose_ClosesIdleConnections()
    {
        (XmlaConnectionPool pool, FakeXmlaConnectionFactory factory, _) = Create();

        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
        }

        pool.Dispose();

        Assert.All(factory.Created, connection => Assert.True(connection.IsDisposed));
    }

    [Fact]
    public async Task Dispose_WithLeaseInFlight_DoesNotThrowAndDiscardsTheConnection()
    {
        (XmlaConnectionPool pool, _, _) = Create();

        IPooledXmlaConnection lease = await pool.RentAsync(KeyA);
        var connection = (FakeXmlaConnection)lease.Connection;

        pool.Dispose();

        // The SemaphoreSlim instances are deliberately not disposed, so this late Release does not
        // throw ObjectDisposedException — it is commented in the pool's Dispose.
        lease.Dispose();

        Assert.True(connection.IsDisposed);
    }

    [Fact]
    public async Task RentAfterDispose_Throws()
    {
        (XmlaConnectionPool pool, _, _) = Create();
        pool.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.RentAsync(KeyA));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        (XmlaConnectionPool pool, _, _) = Create();

        pool.Dispose();
        pool.Dispose();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RentWithBlankKey_Throws(string key)
    {
        (XmlaConnectionPool pool, _, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => pool.RentAsync(key));

        pool.Dispose();
    }

    [Fact]
    public async Task Cancellation_WhileWaitingForASlot_Throws()
    {
        (XmlaConnectionPool pool, _, _) = Create(maxPerKey: 1);
        using var cts = new CancellationTokenSource();

        using IPooledXmlaConnection held = await pool.RentAsync(KeyA);
        Task<IPooledXmlaConnection> blocked = pool.RentAsync(KeyA, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);

        pool.Dispose();
    }

    // ---------- failure while closing ----------

    /// <summary>
    /// A connection that throws while closing does not bring down eviction. It is already dead —
    /// propagating the failure would kill the timer's callback, and from then on <b>no</b> connection would be evicted.
    /// </summary>
    [Fact]
    public async Task AConnectionThatThrowsOnClose_DoesNotBreakEviction()
    {
        (XmlaConnectionPool pool, FakeXmlaConnectionFactory factory, ControllableTimeProvider clock) =
            Create(maxPerKey: 2, idleMinutes: 5);

        factory.Configure = (_, connection) =>
            connection.DisposeFailure = new InvalidOperationException("socket já fechado");

        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
        }

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.All(factory.Created, connection => Assert.True(connection.IsDisposed));

        // And the pool stays usable: eviction stayed alive despite the failure.
        using (IPooledXmlaConnection depois = await pool.RentAsync(KeyA))
        {
        }

        pool.Dispose();
    }

    [Fact]
    public async Task AConnectionThatThrowsOnClose_DoesNotBreakDispose()
    {
        (XmlaConnectionPool pool, FakeXmlaConnectionFactory factory, _) = Create();

        factory.Configure = (_, connection) =>
            connection.DisposeFailure = new InvalidOperationException("socket já fechado");

        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
        }

        pool.Dispose();

        Assert.All(factory.Created, connection => Assert.True(connection.IsDisposed));
    }

    /// <summary>
    /// The eviction timer stays registered after the pool's <c>Dispose</c> — it fires and returns
    /// without touching the already-cleared slots, instead of working over disposed state.
    /// </summary>
    [Fact]
    public async Task TheEvictionTimerAfterDispose_IsANoop()
    {
        (XmlaConnectionPool pool, FakeXmlaConnectionFactory factory, ControllableTimeProvider clock) =
            Create(idleMinutes: 1);

        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
        }

        pool.Dispose();

        // Without the _disposed guard, this advance would run eviction over the already-closed pool.
        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.All(factory.Created, connection => Assert.True(connection.IsDisposed));
    }

    // ---------- state gauges ----------

    /// <summary>
    /// Reads the pool's observable gauges. They are <b>state</b>, not counters: an observer asks
    /// how many connections exist now, and the answer has to come from the pool, not from a sum of events.
    /// </summary>
    private static (int Live, int Idle) ReadGauges()
    {
        int live = 0;
        int idle = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PowerLinqDiagnostics.MeterName
                && instrument.Name.StartsWith("powerlinq.pool.connections.", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };

        // The pool's gauges have no tags, and the Meter belongs to the process: other pools alive
        // in parallel tests publish to the same instruments. That is why the read sums, and the
        // assertions use >= over what THIS pool contributed — never equality over the total.
        listener.SetMeasurementEventCallback<int>((instrument, measurement, _, _) =>
        {
            if (instrument.Name.EndsWith(".live", StringComparison.Ordinal))
                Interlocked.Add(ref live, measurement);
            else
                Interlocked.Add(ref idle, measurement);
        });

        listener.Start();
        listener.RecordObservableInstruments();

        return (live, idle);
    }

    [Fact]
    public async Task TheGauges_CountALentConnectionAsLiveButNotIdle()
    {
        (XmlaConnectionPool pool, _, _) = Create(maxPerKey: 2);

        (int liveAntes, int idleAntes) = ReadGauges();

        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
            (int live, int idle) = ReadGauges();

            // A rented one counts as live — it exists and is open — but not as idle.
            Assert.True(live >= liveAntes + 1, $"viva não subiu: {liveAntes} -> {live}");
            Assert.Equal(idleAntes, idle);
        }

        pool.Dispose();
    }

    [Fact]
    public async Task TheGauges_CountAReturnedConnectionAsBothLiveAndIdle()
    {
        (XmlaConnectionPool pool, _, _) = Create(maxPerKey: 2);

        (int liveAntes, int idleAntes) = ReadGauges();

        using (IPooledXmlaConnection lease = await pool.RentAsync(KeyA))
        {
        }

        (int live, int idle) = ReadGauges();

        Assert.True(live >= liveAntes + 1, $"viva não subiu: {liveAntes} -> {live}");
        Assert.True(idle >= idleAntes + 1, $"ociosa não subiu: {idleAntes} -> {idle}");

        pool.Dispose();
    }

    /// <summary>
    /// The state is summed across keys: two models with one connection each are two live ones, and
    /// it is what separates "the whole pool" from "this slot" on the dashboard.
    /// </summary>
    [Fact]
    public async Task TheGauges_SumAcrossKeys()
    {
        (XmlaConnectionPool pool, _, _) = Create(maxPerKey: 2);

        (int liveAntes, _) = ReadGauges();

        using IPooledXmlaConnection primeiro = await pool.RentAsync(KeyA);
        using IPooledXmlaConnection segundo = await pool.RentAsync(KeyB);

        (int live, _) = ReadGauges();

        Assert.True(live >= liveAntes + 2, $"duas chaves não somaram: {liveAntes} -> {live}");

        pool.Dispose();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen = Volatile.Read(ref target);
        while (value > seen)
        {
            int previous = Interlocked.CompareExchange(ref target, value, seen);
            if (previous == seen)
                return;

            seen = previous;
        }
    }
}
