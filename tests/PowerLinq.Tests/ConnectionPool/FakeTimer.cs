namespace PowerLinq.Tests.ConnectionPool;

internal sealed class FakeTimer(TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period)
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
