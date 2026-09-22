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
}
