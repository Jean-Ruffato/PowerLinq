using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace PowerLinq.ConnectionPool.Diagnostics;

/// <summary>
/// The library's telemetry names and instruments: one <see cref="ActivitySource"/> and one
/// <see cref="Meter"/>, in the shape OpenTelemetry consumes without an adapter.
/// </summary>
/// <remarks>
/// <para>
/// The library used to be opaque in production — no spans, no metrics, and two <c>LogDebug</c>
/// calls in the pool. There was no way to answer <i>which DAX ran, how long it took, did it reuse
/// a connection?</i> without instrumenting from the outside, and instrumenting from the outside
/// was all that was left to whoever consumed the library: an executor decorator whose
/// <c>(dax, elapsed)</c> pair leaked into the signature of every repository method and reached the
/// response DTOs. An infrastructure need contaminated the domain contract.
/// </para>
/// <para>
/// The names are public constants on purpose: whoever configures OpenTelemetry has to write them
/// in <c>AddSource</c> and <c>AddMeter</c>, and a loose literal there breaks silently when it
/// changes.
/// </para>
/// </remarks>
public static class PowerLinqDiagnostics
{
    /// <summary>Name of the <see cref="ActivitySource"/>, for <c>AddSource</c> in OpenTelemetry.</summary>
    public const string ActivitySourceName = "PowerLinq";

    /// <summary>Name of the <see cref="Meter"/>, for <c>AddMeter</c> in OpenTelemetry.</summary>
    public const string MeterName = "PowerLinq";

    /// <summary>Name of the query execution span.</summary>
    public const string QueryActivityName = "powerlinq.query";

    private static readonly string Version =
        typeof(PowerLinqDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

    internal static readonly ActivitySource Source = new(ActivitySourceName, Version);

    private static readonly Meter Meter = new(MeterName, Version);

    /// <summary>Queries executed, with the outcome (<c>ok</c> / <c>error</c>) as a dimension.</summary>
    internal static readonly Counter<long> Queries =
        Meter.CreateCounter<long>("powerlinq.queries", "{query}", "Consultas DAX executadas.");

    /// <summary>Execution duration, from renting the connection to the result being read.</summary>
    internal static readonly Histogram<double> QueryDuration = Meter.CreateHistogram<double>(
        "powerlinq.query.duration", "ms", "Duração da execução de consultas DAX.");

    /// <summary>
    /// Retries caused by a broken session. It gets its own counter because it is the signal of a
    /// dead connection coming back from the pool, and that signal is lost inside the query count.
    /// </summary>
    internal static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        "powerlinq.query.retries", "{retry}", "Consultas repetidas por sessão quebrada.");

    /// <summary>
    /// Wait on the pool's semaphore, per key.
    /// </summary>
    /// <remarks>
    /// This is <b>the</b> saturation signal for <c>MaxConnectionsPerModel</c>: with headroom the
    /// wait stays near zero; when the cap starts to bite it grows before any error shows up.
    /// Without this metric, saturation only manifests as unexplained latency.
    /// </remarks>
    internal static readonly Histogram<double> PoolWait = Meter.CreateHistogram<double>(
        "powerlinq.pool.wait", "ms", "Tempo de espera por uma conexão do pool.");

    /// <summary>Pool rentals, with <c>reused</c> / <c>opened</c> as a dimension.</summary>
    internal static readonly Counter<long> PoolRentals = Meter.CreateCounter<long>(
        "powerlinq.pool.rentals", "{rental}", "Conexões alugadas do pool.");

    /// <summary>
    /// The target dimensions derived from a pool connection string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pool only knows the connection string; the executor knows workspace and dataset.
    /// Labelling the two differently would force every dashboard to join, or to keep two variable
    /// filters for the <b>same</b> concept — and the inconsistency would be frozen into the
    /// dashboards' JSON. Here the string is decomposed into those same two dimensions.
    /// </para>
    /// <para>
    /// The format is what <c>PowerBiOptions.BuildXmlaConnectionString</c> produces, so the
    /// decomposition is deterministic. A string in another format falls back to the raw value as
    /// <c>workspace</c>, which is worse as a label but loses no information.
    /// </para>
    /// <para>
    /// Neither field carries a secret: the pool's string has only <c>Data Source</c> and
    /// <c>Catalog</c>, because authentication happens through the token applied to the connection.
    /// </para>
    /// </remarks>
    internal static KeyValuePair<string, object?>[] TargetTags(string connectionString)
    {
        string? workspace = null;
        string? dataset = null;

        foreach (Range part in connectionString.AsSpan().Split(';'))
        {
            ReadOnlySpan<char> segment = connectionString.AsSpan()[part].Trim();

            if (segment.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                workspace = segment["Data Source=".Length..].ToString();
            else if (segment.StartsWith("Catalog=", StringComparison.OrdinalIgnoreCase))
                dataset = segment["Catalog=".Length..].ToString();
        }

        return
        [
            new("powerlinq.workspace", workspace ?? connectionString),
            new("powerlinq.dataset", dataset ?? string.Empty)
        ];
    }

    /// <summary>
    /// Registers the pool's state observables. Called once, when the pool is constructed.
    /// </summary>
    /// <remarks>
    /// An observable gauge, not a counter: live and idle connections are <b>state</b>, and summing
    /// increments to rebuild it goes wrong on any lost event.
    /// </remarks>
    internal static void RegisterPoolGauges(Func<(int Live, int Idle)> read)
    {
        Meter.CreateObservableGauge(
            "powerlinq.pool.connections.live",
            () => read().Live,
            "{connection}",
            "Conexões abertas no pool.");

        Meter.CreateObservableGauge(
            "powerlinq.pool.connections.idle",
            () => read().Idle,
            "{connection}",
            "Conexões ociosas no pool, disponíveis para empréstimo.");
    }
}
