using PowerLinq.ConnectionPool.Pools;

namespace PowerLinq.ConnectionPool.Options;

/// <summary>
/// Tuning knobs for the XMLA connection pool (<see cref="XmlaConnectionPool"/>). These are the
/// library's own values — the host maps its configuration (appsettings, for example) onto this
/// record when registering the pool.
/// </summary>
/// <param name="MaxConnectionsPerModel">
/// Cap on live XMLA connections per key (endpoint + catalog). Bounds both the concurrency per
/// model/workspace and the total number of open connections. Effective minimum 1.
/// </param>
/// <param name="ConnectionIdleTimeoutMinutes">
/// Time (minutes) a connection may sit idle before eviction closes it. Effective minimum 1.
/// </param>
public sealed record XmlaConnectionPoolOptions(
    int MaxConnectionsPerModel = 4,
    int ConnectionIdleTimeoutMinutes = 15);
