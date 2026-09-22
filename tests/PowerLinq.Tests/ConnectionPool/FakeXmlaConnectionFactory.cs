using PowerLinq.ConnectionPool.Interfaces;

namespace PowerLinq.Tests.ConnectionPool;

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
