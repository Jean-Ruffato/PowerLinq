using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PowerLinq.ConnectionPool.Diagnostics;

namespace PowerLinq.Extensions.DependencyInjection;

/// <summary>Registration of the XMLA endpoint health check.</summary>
public static class HealthCheckServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="XmlaHealthCheck"/>, which probes the endpoint with
    /// <c>EVALUATE ROW("ping", 1)</c>.
    /// </summary>
    /// <param name="builder">The builder returned by <c>AddHealthChecks()</c>.</param>
    /// <param name="name">Name of the check; the default is <see cref="XmlaHealthCheck.Name"/>.</param>
    /// <param name="failureStatus">
    /// Status to report when the check fails. When absent, whatever the check itself decides is
    /// used — <see cref="HealthStatus.Degraded"/> for not configured and for a timeout,
    /// <see cref="HealthStatus.Unhealthy"/> for a connection failure.
    /// </param>
    /// <param name="tags">Tags for filtering the check onto a specific endpoint (<c>ready</c>, for example).</param>
    /// <remarks>
    /// Depends on <c>AddXmlaConnectionPool</c>, which registers the pool and binds <c>PowerBiOptions</c>.
    /// </remarks>
    public static IHealthChecksBuilder AddPowerLinqXmlaCheck(
        this IHealthChecksBuilder builder,
        string name = XmlaHealthCheck.Name,
        HealthStatus? failureStatus = null,
        params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddCheck<XmlaHealthCheck>(name, failureStatus, tags);
    }
}
