using PowerLinq.ConnectionPool.Exceptions;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.DaxConverter.Execution;

namespace PowerLinq.Tests.ConnectionPool;

/// <summary>
/// A controllable <see cref="TimeProvider"/>: time only advances when the test says so, and the
/// timers created by <see cref="CreateTimer"/> fire when the advance passes their due time.
/// </summary>
/// <remarks>
/// It exists so the pool's tests are deterministic without a <c>Sleep</c>.
/// <c>Microsoft.Extensions.TimeProvider.Testing</c> would bring the same, but at the cost of a new
/// dependency just for that; what the pool uses is <see cref="GetUtcNow"/> and a periodic timer.
/// </remarks>
internal sealed class ControllableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
            return _now;
    }

    /// <summary>Advances the clock and fires the timers that came due in the interval.</summary>
    public void Advance(TimeSpan delta)
    {
        FakeTimer[] due;

        lock (_sync)
        {
            _now += delta;
            due = [.. _timers.Where(timer => timer.IsDue(_now))];
        }

        // The callbacks run outside the lock: the pool's calls EvictIdle, which takes PoolSlot's
        // lock, and nesting locks here would only create a deadlock risk that does not exist in
        // production.
        foreach (FakeTimer timer in due)
            timer.Fire(GetUtcNow());
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(callback, state, GetUtcNow() + dueTime, period);

        lock (_sync)
            _timers.Add(timer);

        return timer;
    }

    private sealed class FakeTimer(TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period)
        : ITimer
    {
        private DateTimeOffset _due = due;
        private bool _disposed;

        public bool IsDue(DateTimeOffset now) => !_disposed && now >= _due;

        public void Fire(DateTimeOffset now)
        {
            if (_disposed)
                return;

            _due = period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : now + period;
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A fake XMLA connection: it records calls and allows scripting a failure.</summary>
internal sealed class FakeXmlaConnection(string key) : IXmlaConnection
{
    private int _executions;

    public string Key { get; } = key;

    public bool IsDisposed { get; private set; }

    public int Executions => Volatile.Read(ref _executions);

    /// <summary>When set, <see cref="ExecuteQuery"/> throws whatever the function returns.</summary>
    public Func<int, Exception?>? FailWith { get; set; }

    /// <summary>The result returned when no failure is scripted.</summary>
    public DaxResult Result { get; set; } = new(
        ["[Count]"],
        [new DaxRow(new Dictionary<string, object?> { ["[Count]"] = 7 })]);

    /// <summary>
    /// It observes the token during execution, the way the real connection now does by registering
    /// <c>command.Cancel()</c>: the cancellation arrives as an
    /// <see cref="OperationCanceledException"/>, not as success.
    /// </summary>
    public bool ObservesCancellation { get; set; }

    /// <summary>
    /// Run at the start of <see cref="ExecuteQuery"/>, before the token check. It lets the test
    /// cancel <b>during</b> execution: with the token already cancelled beforehand, the lease fails
    /// at the pool's semaphore and the execution never starts — which is not the scenario the
    /// scenario describes.
    /// </summary>
    public Action? OnExecuting { get; set; }

    /// <summary>
    /// When set, <see cref="Dispose"/> throws. The pool swallows a close failure on purpose — a
    /// connection that is already dead must not bring down eviction or shutdown.
    /// </summary>
    public Exception? DisposeFailure { get; set; }

    /// <summary>The timeout the executor asked for on the last execution.</summary>
    public int? LastCommandTimeoutSeconds { get; private set; }

    /// <summary>The DAX of the last execution.</summary>
    public string? LastQuery { get; private set; }

    /// <summary>How many rows the enumeration actually produced — what measures whether streaming happened.</summary>
    public int StreamedRows { get; private set; }

    /// <summary>Whether the enumeration was closed, by finishing or by abandonment.</summary>
    public bool StreamReleased { get; private set; }

    /// <summary>
    /// It hands the rows back lazily, the way the real connection does with the reader open. The
    /// <c>finally</c> is the analogue of the reader's <c>using</c>: it runs when the consumer
    /// finishes <b>or abandons</b>, because the compiler puts it in the iterator's disposal.
    /// </summary>
    public IEnumerable<DaxRow> StreamQuery(
        string daxQuery, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        LastCommandTimeoutSeconds = commandTimeoutSeconds;
        LastQuery = daxQuery;

        try
        {
            foreach (DaxRow row in Result.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StreamedRows++;
                yield return row;
            }
        }
        finally
        {
            StreamReleased = true;
        }
    }

    public DaxResult ExecuteQuery(string daxQuery, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        int attempt = Interlocked.Increment(ref _executions);
        LastCommandTimeoutSeconds = commandTimeoutSeconds;
        LastQuery = daxQuery;

        OnExecuting?.Invoke();

        if (ObservesCancellation)
            cancellationToken.ThrowIfCancellationRequested();

        if (FailWith?.Invoke(attempt) is { } failure)
            throw failure;

        return Result;
    }

    public void Dispose()
    {
        IsDisposed = true;

        if (DisposeFailure is { } failure)
            throw failure;
    }
}

/// <summary>A fake factory: it counts opens per key and hands back a <see cref="FakeXmlaConnection"/>.</summary>
internal sealed class FakeXmlaConnectionFactory : IXmlaConnectionFactory
{
    private readonly object _sync = new();
    private readonly List<FakeXmlaConnection> _created = [];

    /// <summary>
    /// Applied to each created connection, with the creation index (0-based), to script failures
    /// before first use. The index comes in as an argument because reading it inside the
    /// <c>FailWith</c> would read it at run time, once it has already changed.
    /// </summary>
    public Action<int, FakeXmlaConnection>? Configure { get; set; }

    /// <summary>A wait imposed on creation, to exercise lease races.</summary>
    public Func<Task>? OnCreating { get; set; }

    public IReadOnlyList<FakeXmlaConnection> Created
    {
        get
        {
            lock (_sync)
                return [.. _created];
        }
    }

    public int CreatedCount
    {
        get
        {
            lock (_sync)
                return _created.Count;
        }
    }

    public async Task<IXmlaConnection> CreateOpenConnectionAsync(
        string key,
        string connectionString,
        CancellationToken cancellationToken)
    {
        if (OnCreating is not null)
            await OnCreating();

        var connection = new FakeXmlaConnection(key);

        int index;
        lock (_sync)
        {
            index = _created.Count;
            _created.Add(connection);
        }

        Configure?.Invoke(index, connection);

        return connection;
    }
}

/// <summary>A factory that fails while opening, to verify the semaphore slot does not leak.</summary>
internal sealed class ThrowingXmlaConnectionFactory(Exception failure) : IXmlaConnectionFactory
{
    public int Attempts { get; private set; }

    public Task<IXmlaConnection> CreateOpenConnectionAsync(
        string key,
        string connectionString,
        CancellationToken cancellationToken)
    {
        Attempts++;
        throw failure;
    }
}

internal static class BrokenSession
{
    /// <summary>The exception the pool and the executor treat as a dead session.</summary>
    public static XmlaConnectionBrokenException Create() =>
        new("sessão perdida", new InvalidOperationException("transporte"));
}
