namespace PowerLinq.ConnectionPool.Interfaces;

/// <summary>
/// A lease on a pooled connection. On Dispose the connection goes back to the pool if healthy,
/// or is closed and discarded if <see cref="MarkBroken"/> was called.
/// </summary>
public interface IPooledXmlaConnection : IDisposable
{
    /// <summary>The rented connection. Valid until the lease's <see cref="IDisposable.Dispose"/>.</summary>
    IXmlaConnection Connection { get; }

    /// <summary>Signals that the connection is broken and must not return to the pool.</summary>
    void MarkBroken();
}
