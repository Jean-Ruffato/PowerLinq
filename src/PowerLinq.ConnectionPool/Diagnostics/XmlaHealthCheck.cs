using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.ConnectionPool.Options;
using PowerLinq.DaxConverter.Execution;

namespace PowerLinq.ConnectionPool.Diagnostics;

/// <summary>
/// Health check for the XMLA endpoint: it proves the endpoint, the authentication and that XMLA is
/// enabled on the workspace.
/// </summary>
/// <remarks>
/// <para>
/// The probe DAX is <c>EVALUATE ROW("ping", 1)</c>. A <c>ROW</c> over a literal references no
/// table at all, so it works on <b>any</b> dataset — the check does not need to know the model's
/// schema, and does not break when the model changes.
/// </para>
/// <para>
/// It uses the real pool rather than a separate connection, because that is what a readiness probe
/// has to prove: a check that bypasses the real path can report healthy while traffic fails. The
/// exposure is bounded by <see cref="PowerBiOptions.HealthCheckTimeoutSeconds"/>, which is also
/// the cap on how long the check may hold one of the connections (default: 4 per model).
/// </para>
/// <para>
/// <b>Three states, not two.</b> "Not configured" and "unreachable" call for different actions —
/// the first is a deployment error, the second an incident — and a probe that only says
/// <c>Unhealthy</c> forces someone to read the log to find out which. Saturation is its own case
/// too: blowing the timeout with a full pool is not the same as a broken endpoint, so it becomes
/// <see cref="HealthStatus.Degraded"/> instead of <see cref="HealthStatus.Unhealthy"/>.
/// </para>
/// </remarks>
public sealed class XmlaHealthCheck(
    IXmlaConnectionPool pool,
    IOptions<PowerBiOptions> options) : IHealthCheck
{
    /// <summary>The probe DAX, independent of the model's schema.</summary>
    public const string PingDax = "EVALUATE ROW(\"ping\", 1)";

    /// <summary>Name under which the check is registered by <c>AddPowerLinqHealthCheck</c>.</summary>
    public const string Name = "powerlinq-xmla";

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        PowerBiOptions settings = options.Value;
        Dictionary<string, object> data = Describe(settings);

        if (!settings.IsConfigured)
        {
            return HealthCheckResult.Degraded(
                "O endpoint XMLA não está configurado: informe PowerBi:XmlaEndpoint e PowerBi:Dataset.",
                data: data);
        }

        // A single effective value, floored at 1s, used both for the cancellation and for the
        // command. Computing it in two places left the command with the raw value: with 0
        // configured, the token cancelled after 1s but the command asked the server for timeout 0.
        int seconds = Math.Max(1, settings.HealthCheckTimeoutSeconds);

        // Its own timeout, linked to the caller's token. The linked token makes it possible to
        // tell "the check ran out of its own time" from "the host is shutting down", which are
        // different diagnoses and must not collapse into the same answer.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            using IPooledXmlaConnection lease =
                await pool.RentAsync(settings.BuildXmlaConnectionString(), linked.Token);

            DaxResult result = lease.Connection.ExecuteQuery(PingDax, seconds, linked.Token);

            data["rows"] = result.RowCount;

            return HealthCheckResult.Healthy("O endpoint XMLA respondeu à sondagem.", data);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Saturated pool or slow server: degraded, not broken.
            return HealthCheckResult.Degraded(
                $"A sondagem não respondeu em {seconds}s. " +
                "Pode ser saturação do pool de conexões ou lentidão do servidor.",
                data: data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(
                "A sondagem ao endpoint XMLA falhou.", ex, data);
        }
    }

    /// <summary>
    /// Enough diagnosis to act on, without leaking a secret: the service principal secret
    /// <b>never</b> appears here — only whether it exists, because "no credential configured" is
    /// one of the likely causes of failure.
    /// </summary>
    private static Dictionary<string, object> Describe(PowerBiOptions settings) => new()
    {
        ["endpoint"] = settings.XmlaEndpoint ?? "(não configurado)",
        ["dataset"] = settings.Dataset ?? "(não configurado)",
        ["servicePrincipal"] = settings.HasServicePrincipal,
        ["connectionPoolEnabled"] = settings.ConnectionPoolEnabled,
        ["timeoutSeconds"] = settings.HealthCheckTimeoutSeconds
    };
}
