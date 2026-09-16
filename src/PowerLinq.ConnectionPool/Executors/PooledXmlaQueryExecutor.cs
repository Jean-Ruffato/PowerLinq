using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PowerLinq.ConnectionPool.Diagnostics;
using PowerLinq.ConnectionPool.Exceptions;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.ConnectionPool.Options;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Mapping;

namespace PowerLinq.ConnectionPool.Executors;

/// <summary>
/// <see cref="IDaxQueryExecutor"/> backed by the <see cref="IXmlaConnectionPool"/>: it rents a
/// connection (reused when the pool is on; brand new every time in transient mode), runs the DAX
/// and maps the <see cref="DaxResult"/> onto the target type. If the connection comes back broken
/// (dead session), it discards the connection and retries the query once on a fresh one.
/// </summary>
public sealed class PooledXmlaQueryExecutor
    : IDaxQueryExecutor, IDaxRawQueryExecutor, IDaxStreamingQueryExecutor
{
    private readonly IXmlaConnectionPool _pool;
    private readonly string _connectionString;
    private readonly int _timeoutSeconds;
    private readonly PowerBiOptions _settings;
    private readonly ILogger? _logger;
    private readonly KeyValuePair<string, object?>[] _tags;

    /// <summary>Executor for the configured target — one workspace, one dataset.</summary>
    public PooledXmlaQueryExecutor(
        IXmlaConnectionPool pool,
        IOptions<PowerBiOptions> options,
        ILogger<PooledXmlaQueryExecutor>? logger = null)
        : this(pool, options, target: null, logger) { }

    /// <summary>
    /// Executor for a target resolved at run time. With a null <paramref name="target"/> it is
    /// equivalent to the two-argument constructor.
    /// </summary>
    /// <remarks>
    /// Only the connection string changes — and that is the pool's grouping key. One executor per
    /// request does not multiply connections: two requests for the same target rent from the same
    /// group.
    /// </remarks>
    public PooledXmlaQueryExecutor(
        IXmlaConnectionPool pool,
        IOptions<PowerBiOptions> options,
        DaxTarget? target,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(options);

        _pool = pool;
        _settings = options.Value;
        _logger = logger;
        _connectionString = _settings.BuildXmlaConnectionString(target);
        _timeoutSeconds = _settings.QueryTimeoutSeconds;

        // The target as a dimension of every metric and an attribute of every span. Without it,
        // the telemetry of a multi-tenant process collapses into a single label: one customer
        // with a slow model becomes indistinguishable from a general degradation, which is the
        // question observability exists to answer.
        _tags =
        [
            new("powerlinq.workspace", target?.Workspace ?? _settings.XmlaEndpoint),
            new("powerlinq.dataset", target?.Dataset ?? _settings.Dataset)
        ];
    }

    /// <inheritdoc/>
    public async Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
        where T : class
    {
        DaxResult result = await ExecuteWithRetryAsync(daxQuery, cancellationToken);
        return EntityMapper.MapResult<T>(result);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The connection lease lasts for <b>the whole enumeration</b>, not just the call: the lease's
    /// <c>using</c> sits inside the iterator, so the compiler returns it to the pool when the
    /// consumer finishes — including when the consumer abandons the enumeration through
    /// <c>break</c> or an exception, because then <c>await foreach</c> calls the enumerator's
    /// <c>DisposeAsync</c>.
    /// </para>
    /// <para>
    /// <b>No retry.</b> <see cref="ExecuteAsync{T}"/> retries once when the session drops, and
    /// here that is not possible: if the failure comes after the first row, retrying would hand
    /// back rows the consumer has already seen. Preferring a silently duplicated sequence over an
    /// exception would trade a visible error for an invisible one.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<T> ExecuteStreamAsync<T>(
        string daxQuery,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where T : class
    {
        FrozenDictionary<string, DaxColumnMapping> mappings =
            EntityMapper.GetColumnMappings(typeof(T));

        using IPooledXmlaConnection lease = await _pool.RentAsync(_connectionString, cancellationToken);

        // The token is not observed here: the connection observes it, on every row, and it is the
        // only layer that can actually stop the read — by cancelling the ADOMD command. One more
        // check here changes no test, which is why it was removed.
        foreach (DaxRow row in lease.Connection.StreamQuery(daxQuery, _timeoutSeconds, cancellationToken))
            yield return EntityMapper.MapRow<T>(row, mappings);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The same path as <see cref="ExecuteAsync{T}"/>, without the materialization: it is the read
    /// <c>ToPagedListAsync</c> needs, because the total column does not belong to the contract.
    /// </remarks>
    public Task<DaxResult> ExecuteRowsAsync(
        string daxQuery,
        CancellationToken cancellationToken = default) =>
        ExecuteWithRetryAsync(daxQuery, cancellationToken);

    /// <inheritdoc/>
    public async Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default)
    {
        DaxResult result = await ExecuteWithRetryAsync(daxQuery, cancellationToken);

        // Scalar: first column of the first row (COUNTROWS or SUMX via ROW, for example). The
        // indexer hands back the raw cell, without the defensive conversion of the getters — here
        // the absence of a value must reach the caller as null.
        return result.RowCount == 0 || result.Columns.Count == 0
            ? null
            : result.Rows[0][result.Columns[0]];
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Still reads through <see cref="DaxRow"/>'s defensive getter rather than
    /// <see cref="Convert.ToInt32(object?)"/>: <c>GetInt</c> throws neither on overflow nor on an
    /// invalid format, and changing that would alter the behaviour of a public method already in
    /// use. Anyone who needs a count that does not fit in an <see cref="int"/> uses
    /// <c>LongCountAsync</c>.
    /// </remarks>
    public async Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default)
    {
        DaxResult result = await ExecuteWithRetryAsync(daxQuery, cancellationToken);

        if (result.RowCount == 0 || result.Columns.Count == 0)
            return 0;

        return result.Rows[0].GetInt(result.Columns[0]);
    }

    // Rent -> execute -> return. On a session/transport failure it marks the connection broken
    // (so it does not go back to the pool) and retries ONCE with a fresh connection.
    //
    // The marking comes BEFORE the decision to retry, and not inside a `when (attempt++ == 0)`
    // filter: with the filter, the second failure did not enter the catch, MarkBroken was never
    // called, and the `using` handed the dead connection back to the pool — the next query rented
    // it and failed immediately. A connection that reported a lost session is discarded on both
    // attempts.
    private async Task<DaxResult> ExecuteWithRetryAsync(string daxQuery, CancellationToken cancellationToken)
    {
        using Activity? activity = PowerLinqDiagnostics.Source.StartActivity(
            PowerLinqDiagnostics.QueryActivityName, ActivityKind.Client);

        foreach (KeyValuePair<string, object?> tag in _tags)
            activity?.SetTag(tag.Key, tag.Value);

        // The DAX text is opt-in: it carries the filter values, and the span goes to a backend
        // with its own retention and access rules. Under Debug it always goes out, because there
        // the operator has already chosen to see detail.
        if (_settings.RecordDaxInTelemetry)
            activity?.SetTag("powerlinq.dax", daxQuery);

        _logger?.LogDebug("PowerLinq executando DAX: {Dax}", daxQuery);

        long start = Stopwatch.GetTimestamp();
        try
        {
            DaxResult result = await ExecuteCoreAsync(daxQuery, cancellationToken);
            Record(activity, start, daxQuery, outcome: "ok");
            return result;
        }
        catch (Exception ex)
        {
            Record(activity, start, daxQuery, outcome: "error");
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private void Record(Activity? activity, long start, string daxQuery, string outcome)
    {
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        KeyValuePair<string, object?>[] tags = [.. _tags, new("powerlinq.outcome", outcome)];
        PowerLinqDiagnostics.Queries.Add(1, tags);
        PowerLinqDiagnostics.QueryDuration.Record(elapsed, tags);
        activity?.SetTag("powerlinq.duration_ms", elapsed);

        // A slow query shows up at Information, where production can see it; the fast ones do not
        // flood the log. A threshold of zero turns this off.
        //
        // The DAX only appears here with the opt-in on. Information is on BY DEFAULT, so printing
        // it unconditionally defeated the span's opt-in: one query crossing the threshold was
        // enough to send the filter values to the log without anyone having decided so. Without
        // the text the line still says what matters — what was slow and where — and the DAX stays
        // reachable through the trace.
        if (_settings.SlowQueryLogThresholdMs > 0 && elapsed >= _settings.SlowQueryLogThresholdMs)
        {
            if (_settings.RecordDaxInTelemetry)
            {
                _logger?.LogInformation(
                    "PowerLinq: consulta DAX levou {ElapsedMs:F0}ms ({Outcome}) em {Workspace}/{Dataset}, acima do limiar de {ThresholdMs}ms. DAX: {Dax}",
                    elapsed, outcome, _tags[0].Value, _tags[1].Value, _settings.SlowQueryLogThresholdMs, daxQuery);
            }
            else
            {
                _logger?.LogInformation(
                    "PowerLinq: consulta DAX levou {ElapsedMs:F0}ms ({Outcome}) em {Workspace}/{Dataset}, acima do limiar de {ThresholdMs}ms. O texto do DAX é omitido; ligue PowerBi:RecordDaxInTelemetry para incluí-lo.",
                    elapsed, outcome, _tags[0].Value, _tags[1].Value, _settings.SlowQueryLogThresholdMs);
            }
        }
    }

    private async Task<DaxResult> ExecuteCoreAsync(string daxQuery, CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (true)
        {
            using IPooledXmlaConnection lease = await _pool.RentAsync(_connectionString, cancellationToken);
            try
            {
                return lease.Connection.ExecuteQuery(daxQuery, _timeoutSeconds, cancellationToken);
            }
            catch (XmlaConnectionBrokenException)
            {
                lease.MarkBroken();

                if (attempt++ > 0)
                    throw;

                PowerLinqDiagnostics.Retries.Add(1, _tags);
            }
            // Query cancelled: discard the connection instead of returning it to the pool, and do
            // not retry — cancellation is the caller's decision, not a failure to recover from.
            //
            // Discarding is the conservative choice. After Cancel() the session state is
            // indeterminate (there may be an unread partial result), and returning a possibly
            // poisoned connection contaminates the next query that rents it. Closing a connection
            // is cheap; diagnosing an intermittent pool is not. If the connection is ever shown to
            // be reusable after a cancellation, this can be relaxed — but that needs a real
            // endpoint to verify.
            catch (OperationCanceledException)
            {
                lease.MarkBroken();
                throw;
            }
        }
    }
}
