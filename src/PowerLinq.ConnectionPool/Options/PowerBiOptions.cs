using System.Text;
using PowerLinq.ConnectionPool.Pools;
using PowerLinq.ConnectionPool.Providers;
using PowerLinq.DaxConverter.Execution;

namespace PowerLinq.ConnectionPool.Options;

/// <summary>
/// Configuration for the XMLA connection to a real Power BI workspace. Bound to the
/// <c>PowerBi</c> configuration section (appsettings, environment variables or user-secrets).
/// The service principal secret must <b>never</b> go into a versioned appsettings — use
/// user-secrets or an environment variable.
/// </summary>
public sealed class PowerBiOptions
{
    /// <summary>Path of the section in the configuration.</summary>
    public const string SectionName = "PowerBi";

    /// <summary>For example: <c>powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace</c></summary>
    public string? XmlaEndpoint { get; set; }

    /// <summary>Name of the dataset (semantic model) published in the workspace.</summary>
    public string? Dataset { get; set; }

    // --- Service principal (the recommended authentication for server/Linux) ---

    /// <summary>Entra ID directory (tenant) where the service principal is registered.</summary>
    public string? TenantId { get; set; }
    /// <summary>Application (service principal) identifier.</summary>
    public string? ClientId { get; set; }
    /// <summary>
    /// Service principal secret. <b>Never</b> in a versioned appsettings — use user-secrets or an
    /// environment variable.
    /// </summary>
    public string? ClientSecret { get; set; }

    // --- Connection pool + AAD token (ADR-024) ---

    /// <summary>Cap on live XMLA connections per key (endpoint + catalog) in the pool. Effective minimum 1.</summary>
    public int MaxConnectionsPerModel { get; set; } = 4;

    /// <summary>
    /// Minutes a connection may sit idle before eviction closes it. Effective minimum 1.
    /// </summary>
    public int ConnectionIdleTimeoutMinutes { get; set; } = 15;

    /// <summary>Margin (s) before the AAD token expires within which it is renewed proactively.</summary>
    public int TokenRefreshMarginSeconds { get; set; } = 300;

    /// <summary>Timeout (s) for executing each DAX query through the pool.</summary>
    public int QueryTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Health check timeout (s), independent of <see cref="QueryTimeoutSeconds"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately short. A readiness probe that waits 120 s is not a probe: the orchestrator has
    /// already pulled the pod out of rotation, or given up, long before the answer arrives. This
    /// cap also bounds how long the check can hold one of the pool's connections, whose default
    /// cap is 4 per model.
    /// </remarks>
    public int HealthCheckTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Includes the DAX text in telemetry: as a span attribute and in the slow-query line.
    /// <b>Off by default.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opt-in because the DAX carries the filter <b>values</b> — tax id, customer identifier,
    /// name — and spans and logs go to backends with their own retention and access control,
    /// different from the database's. Turning this on is a decision for someone who knows the
    /// data, not a library default.
    /// </para>
    /// <para>
    /// It governs <b>both</b> outputs on purpose. It used to cover only the span, and the
    /// slow-query line — which is <c>Information</c>, therefore <b>on by default</b> — printed the
    /// whole DAX anyway. That defeated the opt-in: one query crossing the threshold was enough to
    /// send the filter values to the log without anyone having decided so.
    /// </para>
    /// <para>
    /// <c>Debug</c> keeps printing the DAX regardless of this setting, and the distinction is a
    /// single one: <c>Debug</c> is <b>off</b> by default, so enabling it is already the explicit
    /// act. This option governs what escapes under the default configuration.
    /// </para>
    /// </remarks>
    public bool RecordDaxInTelemetry { get; set; }

    /// <summary>
    /// Above this time (ms), the execution is logged at <c>Information</c> with its duration.
    /// <c>0</c> turns it off.
    /// </summary>
    /// <remarks>
    /// The generated DAX always goes out at <c>Debug</c>; this threshold exists so a slow query
    /// shows up in production, where <c>Debug</c> is off, without flooding the log with fast ones.
    /// </remarks>
    public int SlowQueryLogThresholdMs { get; set; } = 1000;

    /// <summary>
    /// Feature flag (ADR-024): when <c>true</c> (the default), uses the singleton pool + the
    /// cached/renewed token; when <c>false</c>, falls back to the legacy behaviour (a fresh
    /// connection and token per query) — a quick rollback.
    /// </summary>
    public bool ConnectionPoolEnabled { get; set; } = true;

    /// <summary>Endpoint and dataset are present — the minimum needed to attempt a connection.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(XmlaEndpoint) && !string.IsNullOrWhiteSpace(Dataset);

    /// <summary>
    /// A complete service principal credential is present. Without it, authentication falls back
    /// to the default chain, which is interactive and does not work on a headless server.
    /// </summary>
    public bool HasServicePrincipal =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>
    /// Projects the app configuration onto the library's own auth options, consumed by the token
    /// providers (<see cref="XmlaAccessTokenProvider"/> / <see cref="TransientXmlaAccessTokenProvider"/>).
    /// </summary>
    public XmlaAuthOptions ToAuthOptions() =>
        new(
            TenantId ?? string.Empty,
            ClientId ?? string.Empty,
            ClientSecret ?? string.Empty,
            TokenRefreshMarginSeconds);

    /// <summary>Projects the app configuration onto <see cref="XmlaConnectionPool"/>'s own options.</summary>
    public XmlaConnectionPoolOptions ToConnectionPoolOptions() =>
        new(MaxConnectionsPerModel, ConnectionIdleTimeoutMinutes);

    /// <summary>
    /// Builds the ADOMD.NET connection string. With a service principal it injects
    /// <c>User ID=app:{clientId}@{tenantId}</c> and the password; without one it leaves the job to
    /// the default authentication (interactive — it does not work on a headless server).
    /// </summary>
    public string BuildConnectionString()
    {
        StringBuilder sb = new StringBuilder()
            .Append("Data Source=").Append(XmlaEndpoint).Append(';')
            .Append("Catalog=").Append(Dataset).Append(';');

        if (HasServicePrincipal)
        {
            sb.Append("User ID=app:").Append(ClientId).Append('@').Append(TenantId).Append(';')
              .Append("Password=").Append(ClientSecret).Append(';');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Connection string WITHOUT credentials (only <c>Data Source</c> + <c>Catalog</c>), used by
    /// the pool: it authenticates through the AAD token applied to the connection
    /// (<see cref="XmlaAccessTokenProvider"/>), not through the connection string. It is also the
    /// per-model pool key (endpoint + catalog).
    /// </summary>
    /// <param name="target">
    /// An explicit target, for the multi-tenant case. When absent, uses <see cref="XmlaEndpoint"/>
    /// and <see cref="Dataset"/> from configuration — the long-standing behaviour.
    /// </param>
    /// <remarks>
    /// The string is the pool's grouping <b>key</b>, so different targets produce different keys
    /// and connections keep being reused per target, not per query.
    /// </remarks>
    public string BuildXmlaConnectionString(DaxTarget? target = null) =>
        new StringBuilder()
            .Append("Data Source=").Append(target?.Workspace ?? XmlaEndpoint).Append(';')
            .Append("Catalog=").Append(target?.Dataset ?? Dataset).Append(';')
            .ToString();
}
