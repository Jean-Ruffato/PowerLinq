using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PowerLinq.ConnectionPool.Diagnostics;
using PowerLinq.ConnectionPool.Executors;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.ConnectionPool.Options;
using PowerLinq.ConnectionPool.Pools;
using PowerLinq.DaxConverter.Execution;

namespace PowerLinq.Tests.ConnectionPool;

/// <summary>
/// Query execution telemetry: span, metrics and log.
/// </summary>
/// <remarks>
/// <para>
/// The library was opaque — no <c>ActivitySource</c>, no <c>Meter</c>, and two <c>LogDebug</c>
/// calls in the pool. Instrumenting from the outside was all that was left to consumers, and the
/// decorator's <c>(dax, elapsed)</c> pair leaked into the signature of every repository method until it reached the response DTOs.
/// </para>
/// <para>
/// <b>Every collection here filters by a <c>dataset</c> unique to the test.</b>
/// <see cref="ActivitySource"/> and <see cref="Meter"/> belong to the <b>process</b>, and test
/// classes run in parallel: without the filter, a test measures what another test did, from
/// another thread. The first version of these tests used <c>Assert.Single</c> over everything and
/// failed intermittently — it passed in isolation and failed in the full suite.
/// </para>
/// </remarks>
public sealed class ObservabilityTests
{
    private sealed class Linha
    {
        public int Valor { get; set; }
    }

    private sealed class StubPool(FakeXmlaConnection connection) : IXmlaConnectionPool
    {
        public Task<IPooledXmlaConnection> RentAsync(
            string connectionString, CancellationToken cancellationToken = default) =>
            Task.FromResult<IPooledXmlaConnection>(new Lease(connection));

        private sealed class Lease(FakeXmlaConnection connection) : IPooledXmlaConnection
        {
            public IXmlaConnection Connection => connection;

            public void MarkBroken() { }

            public void Dispose() { }
        }
    }

    private static PowerBiOptions SettingsFor(string dataset) => new()
    {
        XmlaEndpoint = "powerbi://api.powerbi.com/v1.0/myorg/Workspace",
        Dataset = dataset
    };

    private static PooledXmlaQueryExecutor Executor(FakeXmlaConnection connection, PowerBiOptions settings) =>
        new(new StubPool(connection), Options.Create(settings));

    /// <summary>Collects the library's spans whose <c>dataset</c> is the given one.</summary>
    /// <remarks>
    /// With no listener registered, <c>StartActivity</c> returns <see langword="null"/> — so a test
    /// that does not listen would pass even with no instrumentation at all.
    /// </remarks>
    private sealed class SpanCollector : IDisposable
    {
        private readonly object _sync = new();
        private readonly List<Activity> _spans = [];
        private readonly ActivityListener _listener;

        public SpanCollector(string dataset)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == PowerLinqDiagnostics.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.GetTagItem("powerlinq.dataset") as string != dataset)
                        return;

                    lock (_sync)
                        _spans.Add(activity);
                }
            };

            ActivitySource.AddActivityListener(_listener);
        }

        /// <summary>This test's only span.</summary>
        public Activity Single()
        {
            lock (_sync)
                return Assert.Single(_spans);
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Collects the library's measurements whose <c>dataset</c> is the given one.</summary>
    private sealed class MetricCollector : IDisposable
    {
        private readonly object _sync = new();
        private readonly List<(string Name, long Value, string? Outcome)> _counters = [];
        private readonly List<string> _histograms = [];
        private readonly MeterListener _listener = new();

        public MetricCollector(string dataset)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == PowerLinqDiagnostics.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };

            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (Tag(tags, "powerlinq.dataset") != dataset)
                    return;

                lock (_sync)
                    _counters.Add((instrument.Name, value, Tag(tags, "powerlinq.outcome")));
            });

            _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            {
                if (Tag(tags, "powerlinq.dataset") != dataset)
                    return;

                lock (_sync)
                    _histograms.Add(instrument.Name);
            });

            _listener.Start();
        }

        public List<(string Name, long Value, string? Outcome)> Counters
        {
            get { lock (_sync) return [.. _counters]; }
        }

        public List<string> Histograms
        {
            get { lock (_sync) return [.. _histograms]; }
        }

        private static string? Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == key)
                    return tag.Value?.ToString();
            }

            return null;
        }

        public void Dispose() => _listener.Dispose();
    }

    // ---------- span ----------

    [Fact]
    public async Task AnExecutionEmitsASpanWithDurationAndModel()
    {
        const string dataset = nameof(AnExecutionEmitsASpanWithDurationAndModel);
        using var spans = new SpanCollector(dataset);

        await Executor(new FakeXmlaConnection("ok"), SettingsFor(dataset)).ExecuteAsync<Linha>("EVALUATE T");

        Activity span = spans.Single();
        Assert.Equal(PowerLinqDiagnostics.QueryActivityName, span.OperationName);
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/Workspace", span.GetTagItem("powerlinq.workspace"));
        Assert.Equal(dataset, span.GetTagItem("powerlinq.dataset"));
        Assert.NotNull(span.GetTagItem("powerlinq.duration_ms"));
    }

    [Fact]
    public async Task TheDaxTextIsNotRecordedByDefault()
    {
        // Opt-in because the DAX carries the filter VALUES, and the span goes to a backend with
        // retention and access control of its own, different from the database's.
        const string dataset = nameof(TheDaxTextIsNotRecordedByDefault);
        using var spans = new SpanCollector(dataset);

        await Executor(new FakeXmlaConnection("ok"), SettingsFor(dataset))
            .ExecuteAsync<Linha>("EVALUATE FILTER(T, T[Cnpj] = \"12345678000199\")");

        Assert.Null(spans.Single().GetTagItem("powerlinq.dax"));
    }

    [Fact]
    public async Task TheDaxTextIsRecordedWhenExplicitlyEnabled()
    {
        const string dataset = nameof(TheDaxTextIsRecordedWhenExplicitlyEnabled);
        PowerBiOptions settings = SettingsFor(dataset);
        settings.RecordDaxInTelemetry = true;

        using var spans = new SpanCollector(dataset);

        await Executor(new FakeXmlaConnection("ok"), settings).ExecuteAsync<Linha>("EVALUATE T");

        Assert.Equal("EVALUATE T", spans.Single().GetTagItem("powerlinq.dax"));
    }

    [Fact]
    public async Task AFailedExecutionMarksTheSpanAsError()
    {
        const string dataset = nameof(AFailedExecutionMarksTheSpanAsError);
        var connection = new FakeXmlaConnection("falha");
        connection.FailWith = _ => BrokenSession.Create();

        using var spans = new SpanCollector(dataset);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Executor(connection, SettingsFor(dataset)).ExecuteAsync<Linha>("EVALUATE T"));

        Assert.Equal(ActivityStatusCode.Error, spans.Single().Status);
    }

    [Fact]
    public async Task TheSpanCarriesTheExplicitTargetWhenThereIsOne()
    {
        // The multi-tenant hook: without the target on the span, the telemetry of a process with
        // many workspaces collapses into a single label, and a customer with a slow model becomes
        // indistinguishable from a general degradation.
        const string dataset = nameof(TheSpanCarriesTheExplicitTargetWhenThereIsOne);
        var executor = new PooledXmlaQueryExecutor(
            new StubPool(new FakeXmlaConnection("ok")),
            Options.Create(SettingsFor("ModeloDaConfiguracao")),
            new DaxTarget("powerbi://api.powerbi.com/v1.0/myorg/ClienteA", dataset));

        using var spans = new SpanCollector(dataset);

        await executor.ExecuteAsync<Linha>("EVALUATE T");

        Activity span = spans.Single();
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/ClienteA", span.GetTagItem("powerlinq.workspace"));
        Assert.Equal(dataset, span.GetTagItem("powerlinq.dataset"));
    }

    // ---------- metrics ----------

    [Fact]
    public async Task AnExecutionRecordsTheQueryCounterAndDuration()
    {
        const string dataset = nameof(AnExecutionRecordsTheQueryCounterAndDuration);
        using var metrics = new MetricCollector(dataset);

        await Executor(new FakeXmlaConnection("ok"), SettingsFor(dataset)).ExecuteAsync<Linha>("EVALUATE T");

        Assert.Contains(("powerlinq.queries", 1L, "ok"), metrics.Counters);
        Assert.Contains("powerlinq.query.duration", metrics.Histograms);
    }

    [Fact]
    public async Task AFailureIsCountedAsError()
    {
        const string dataset = nameof(AFailureIsCountedAsError);
        using var metrics = new MetricCollector(dataset);

        var connection = new FakeXmlaConnection("falha");
        connection.FailWith = _ => BrokenSession.Create();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Executor(connection, SettingsFor(dataset)).ExecuteAsync<Linha>("EVALUATE T"));

        Assert.Contains(("powerlinq.queries", 1L, "error"), metrics.Counters);
        Assert.DoesNotContain(("powerlinq.queries", 1L, "ok"), metrics.Counters);
    }

    [Fact]
    public async Task ARetryIsCountedSeparatelyFromTheQuery()
    {
        // Its own counter because a retry is the signal of a dead connection coming back from the
        // pool, and it gets lost inside the query count.
        const string dataset = nameof(ARetryIsCountedSeparatelyFromTheQuery);
        using var metrics = new MetricCollector(dataset);

        var connection = new FakeXmlaConnection("intermitente");
        connection.FailWith = attempt => attempt == 1 ? BrokenSession.Create() : null;

        await Executor(connection, SettingsFor(dataset)).ExecuteAsync<Linha>("EVALUATE T");

        Assert.Contains(metrics.Counters, measurement => measurement.Name == "powerlinq.query.retries");
        Assert.Contains(("powerlinq.queries", 1L, "ok"), metrics.Counters);
        Assert.Equal(2, connection.Executions);
    }

    // ---------- the opt-in governs BOTH outputs ----------

    /// <summary>A logger that keeps the formatted messages, to inspect what would leak.</summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) return [.. _messages]; }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
                _messages.Add($"{logLevel}: {formatter(state, exception)}");
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }

    private static PooledXmlaQueryExecutor ExecutorWithLogger(
        FakeXmlaConnection connection, PowerBiOptions settings, ILogger logger) =>
        new(new StubPool(connection), Options.Create(settings), target: null, logger);

    /// <summary>
    /// The DAX on the span was opt-in <b>because it carries the filter values</b>, but the
    /// slow-query line is <c>Information</c> — on by default — and printed the whole DAX anyway.
    /// One query crossing the threshold was enough for the values to reach the log with nobody deciding so.
    /// </summary>
    [Fact]
    public async Task WithTheOptInOff_TheSlowQueryLogDoesNotCarryTheDaxText()
    {
        const string cnpjNoFiltro = "12345678000199";
        PowerBiOptions settings = SettingsFor(nameof(WithTheOptInOff_TheSlowQueryLogDoesNotCarryTheDaxText));
        settings.RecordDaxInTelemetry = false;
        // A threshold of 0 would turn the log off; 1 ms guarantees every execution counts as slow.
        settings.SlowQueryLogThresholdMs = 1;

        var logger = new CapturingLogger();
        var connection = new FakeXmlaConnection("lenta");
        connection.OnExecuting = () => Thread.Sleep(5);

        await ExecutorWithLogger(connection, settings, logger)
            .ExecuteAsync<Linha>($"EVALUATE FILTER(T, T[Cnpj] = \"{cnpjNoFiltro}\")");

        string informacao = Assert.Single(logger.Messages, m => m.StartsWith("Information", StringComparison.Ordinal));

        // The line exists and says what matters...
        Assert.Contains("acima do limiar", informacao);
        // ...but the filter's value does not appear in it.
        Assert.DoesNotContain(cnpjNoFiltro, informacao);
        Assert.Contains("RecordDaxInTelemetry", informacao);
    }

    [Fact]
    public async Task WithTheOptInOn_TheSlowQueryLogCarriesTheDax()
    {
        PowerBiOptions settings = SettingsFor(nameof(WithTheOptInOn_TheSlowQueryLogCarriesTheDax));
        settings.RecordDaxInTelemetry = true;
        settings.SlowQueryLogThresholdMs = 1;

        var logger = new CapturingLogger();
        var connection = new FakeXmlaConnection("lenta");
        connection.OnExecuting = () => Thread.Sleep(5);

        await ExecutorWithLogger(connection, settings, logger).ExecuteAsync<Linha>("EVALUATE Tabela");

        string informacao = Assert.Single(logger.Messages, m => m.StartsWith("Information", StringComparison.Ordinal));
        Assert.Contains("EVALUATE Tabela", informacao);
    }

    [Fact]
    public async Task AFastQueryDoesNotProduceTheSlowLine()
    {
        PowerBiOptions settings = SettingsFor(nameof(AFastQueryDoesNotProduceTheSlowLine));
        settings.SlowQueryLogThresholdMs = 60_000;

        var logger = new CapturingLogger();

        await ExecutorWithLogger(new FakeXmlaConnection("rapida"), settings, logger)
            .ExecuteAsync<Linha>("EVALUATE Tabela");

        Assert.DoesNotContain(logger.Messages, m => m.StartsWith("Information", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>Debug</c> still carries the DAX regardless of the opt-in, and the distinction is a single
    /// one: it is <b>off</b> by default, so enabling it is already the explicit act.
    /// </summary>
    [Fact]
    public async Task TheDebugLineCarriesTheDaxRegardlessOfTheOptIn()
    {
        PowerBiOptions settings = SettingsFor(nameof(TheDebugLineCarriesTheDaxRegardlessOfTheOptIn));
        settings.RecordDaxInTelemetry = false;

        var logger = new CapturingLogger();

        await ExecutorWithLogger(new FakeXmlaConnection("ok"), settings, logger)
            .ExecuteAsync<Linha>("EVALUATE Tabela");

        Assert.Contains(logger.Messages, m => m.StartsWith("Debug", StringComparison.Ordinal) && m.Contains("EVALUATE Tabela"));
    }

    // ---------- the pool uses the executor's dimensions ----------

    /// <summary>
    /// The pool labelled with the whole connection string (<c>pool.key</c>) while the executor
    /// labelled with <c>workspace</c> and <c>dataset</c> — the same concept in two forms, which
    /// would force every dashboard to join, or to keep two variable filters for the same thing, and
    /// the inconsistency would be frozen into the dashboards' JSON.
    /// </summary>
    /// <remarks>
    /// It tests through observable behaviour — the metric the pool emits — and not through the
    /// helper that decomposes the string: it is the published dimension the dashboard consumes.
    /// </remarks>
    [Fact]
    public async Task ThePoolLabelsWithWorkspaceAndDataset_NotWithTheRawKey()
    {
        const string dataset = nameof(ThePoolLabelsWithWorkspaceAndDataset_NotWithTheRawKey);
        var labels = new List<string>();
        var seen = new List<KeyValuePair<string, object?>>();

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PowerLinqDiagnostics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "powerlinq.dataset" && tag.Value as string == dataset)
                {
                    lock (labels)
                    {
                        labels.Add(instrument.Name);
                        seen.AddRange(tags.ToArray());
                    }

                    return;
                }
            }
        });
        meterListener.Start();

        var pool = new XmlaConnectionPool(
            new FakeXmlaConnectionFactory(),
            new XmlaConnectionPoolOptions(2, 15),
            new ControllableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<XmlaConnectionPool>.Instance);

        using (pool)
        {
            using IPooledXmlaConnection lease =
                await pool.RentAsync($"Data Source=workspace-de-teste;Catalog={dataset};");
        }

        Assert.Contains("powerlinq.pool.rentals", labels);
        Assert.Contains(seen, tag => tag.Key == "powerlinq.workspace" && (string?)tag.Value == "workspace-de-teste");

        // The old label must no longer exist: it was the whole connection string.
        Assert.DoesNotContain(seen, tag => tag.Key == "powerlinq.pool.key");
    }

    [Fact]
    public void TheConnectionStringCarriesNoSecretToLeakAsALabel()
    {
        // The pool's string has only Data Source and Catalog: authentication happens through the
        // token applied to the connection, not through the connection string. That is what makes it safe to use as a dimension.
        var settings = new PowerBiOptions
        {
            XmlaEndpoint = "endpoint",
            Dataset = "modelo",
            TenantId = "t",
            ClientId = "c",
            ClientSecret = "segredo-que-nao-pode-vazar"
        };

        Assert.DoesNotContain("segredo-que-nao-pode-vazar", settings.BuildXmlaConnectionString());
    }

    // ---------- public names ----------

    [Fact]
    public void TheSourceAndMeterNamesArePublicConstants()
    {
        // Whoever configures OpenTelemetry writes these names in AddSource and AddMeter. A loose
        // literal there breaks silently when the name changes.
        Assert.Equal("PowerLinq", PowerLinqDiagnostics.ActivitySourceName);
        Assert.Equal("PowerLinq", PowerLinqDiagnostics.MeterName);
        Assert.Equal("powerlinq.query", PowerLinqDiagnostics.QueryActivityName);
    }
}
