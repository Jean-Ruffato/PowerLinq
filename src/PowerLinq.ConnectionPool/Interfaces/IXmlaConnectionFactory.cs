namespace PowerLinq.ConnectionPool.Interfaces;

/// <summary>
/// Creates open XMLA connections (applies the token + refresh callback and calls <c>Open()</c>).
/// Test seam: the pool depends on this factory, not on ADOMD.NET directly.
/// </summary>
public interface IXmlaConnectionFactory
{
    /// <summary>Opens a connection that is already authenticated and ready to execute.</summary>
    Task<IXmlaConnection> CreateOpenConnectionAsync(
        string key,
        string connectionString,
        CancellationToken cancellationToken);
}
