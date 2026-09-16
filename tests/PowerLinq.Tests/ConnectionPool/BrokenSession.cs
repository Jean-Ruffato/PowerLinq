using PowerLinq.ConnectionPool.Exceptions;

namespace PowerLinq.Tests.ConnectionPool;

internal static class BrokenSession
{
    /// <summary>The exception the pool and the executor treat as a dead session.</summary>
    public static XmlaConnectionBrokenException Create() =>
        new("sessão perdida", new InvalidOperationException("transporte"));
}
