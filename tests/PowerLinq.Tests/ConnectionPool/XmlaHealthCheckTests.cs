using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PowerLinq.ConnectionPool.Diagnostics;
using PowerLinq.ConnectionPool.Exceptions;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.ConnectionPool.Options;
using PowerLinq.Extensions.DependencyInjection;

namespace PowerLinq.Tests.ConnectionPool;

/// <summary>
/// Health check of the XMLA endpoint.
/// </summary>
/// <remarks>
/// Whoever consumed the library implemented this by hand, with the same probe DAX and the same
/// error classification — a sign the gap was the library's. What these tests pin down is what a
/// probe has to get right: telling <b>not configured</b> from <b>unreachable</b> from <b>slow</b>,
/// because the three call for different actions.
/// </remarks>
public sealed class XmlaHealthCheckTests
{
    private static PowerBiOptions Configured() => new()
    {
        XmlaEndpoint = "powerbi://api.powerbi.com/v1.0/myorg/Workspace",
        Dataset = "Modelo",
        HealthCheckTimeoutSeconds = 1
    };

    private static XmlaHealthCheck Check(IXmlaConnectionPool pool, PowerBiOptions? settings = null) =>
        new(pool, Options.Create(settings ?? Configured()));

    private static Task<HealthCheckResult> Run(XmlaHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext());

    /// <summary>A pool that returns a configurable connection and records the rented keys.</summary>
    private sealed class StubPool(FakeXmlaConnection connection) : IXmlaConnectionPool
    {
        public List<string> Rentals { get; } = [];

        public Task<IPooledXmlaConnection> RentAsync(
            string connectionString, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rentals.Add(connectionString);
            return Task.FromResult<IPooledXmlaConnection>(new Lease(connection));
        }

        private sealed class Lease(FakeXmlaConnection connection) : IPooledXmlaConnection
        {
            public IXmlaConnection Connection => connection;

            public bool Broken { get; private set; }

            public void MarkBroken() => Broken = true;

            public void Dispose() { }
        }
    }

    /// <summary>A pool that never returns a connection — it mimics saturation, and only lets go on cancellation.</summary>
    private sealed class SaturatedPool : IXmlaConnectionPool
    {
        public async Task<IPooledXmlaConnection> RentAsync(
            string connectionString, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }

        private sealed class UnreachableException : Exception;
    }

    // ---------- the three states ----------

    [Fact]
    public async Task WhenTheEndpointAnswers_TheCheckIsHealthy()
    {
        var connection = new FakeXmlaConnection("ping");
        var pool = new StubPool(connection);

        HealthCheckResult result = await Run(Check(pool));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(XmlaHealthCheck.PingDax, connection.LastQuery);
    }

    [Fact]
    public async Task WhenNotConfigured_TheCheckIsDegraded_NotUnhealthy()
    {
        // A deployment error, not an incident: a probe answering Unhealthy in both cases forces
        // someone to read the log to find out which.
        var pool = new StubPool(new FakeXmlaConnection("ping"));

        HealthCheckResult result = await Run(Check(pool, new PowerBiOptions()));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("não está configurado", result.Description);
        Assert.Empty(pool.Rentals);
    }

    [Fact]
    public async Task WhenTheConnectionFails_TheCheckIsUnhealthyAndCarriesTheException()
    {
        var connection = new FakeXmlaConnection("ping");
        connection.FailWith = _ => BrokenSession.Create();

        HealthCheckResult result = await Run(Check(new StubPool(connection)));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.IsType<XmlaConnectionBrokenException>(result.Exception);
    }

    [Fact]
    public async Task WhenThePoolIsSaturated_TheCheckIsDegraded_NotUnhealthy()
    {
        // Saturation is not a broken endpoint. Reporting Unhealthy here would pull the pod out of
        // rotation precisely while it is serving traffic.
        HealthCheckResult result = await Run(Check(new SaturatedPool()));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("não respondeu em 1s", result.Description);
    }

    // ---------- its own timeout ----------

    [Fact]
    public async Task TheCheckUsesItsOwnTimeout_NotTheQueryTimeout()
    {
        var connection = new FakeXmlaConnection("ping");
        var settings = Configured();
        settings.QueryTimeoutSeconds = 120;
        settings.HealthCheckTimeoutSeconds = 7;

        await Run(Check(new StubPool(connection), settings));

        // What guarantees the probe does not hang for 120 s.
        Assert.Equal(7, connection.LastCommandTimeoutSeconds);
    }

    [Fact]
    public async Task ATimeoutOfZero_IsRaisedToOneSecond()
    {
        // An invalid configuration must not become an instant timeout, which would report Degraded
        // every time and make the probe look broken.
        var connection = new FakeXmlaConnection("ping");
        var settings = Configured();
        settings.HealthCheckTimeoutSeconds = 0;

        HealthCheckResult result = await Run(Check(new StubPool(connection), settings));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(1, connection.LastCommandTimeoutSeconds);
    }

    [Fact]
    public async Task TheCallersCancellation_PropagatesInsteadOfBecomingDegraded()
    {
        // A host shutdown is not a health diagnosis: it has to surface as a cancellation.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Check(new StubPool(new FakeXmlaConnection("ping")))
                .CheckHealthAsync(new HealthCheckContext(), cancelled.Token));
    }

    // ---------- diagnosis ----------

    [Fact]
    public async Task TheResultCarriesDiagnosticsWithoutLeakingTheSecret()
    {
        var settings = Configured();
        settings.TenantId = "tenant";
        settings.ClientId = "client";
        settings.ClientSecret = "segredo-que-nao-pode-vazar";

        HealthCheckResult result = await Run(Check(new StubPool(new FakeXmlaConnection("ping")), settings));

        Assert.Equal(settings.XmlaEndpoint, result.Data["endpoint"]);
        Assert.Equal("Modelo", result.Data["dataset"]);
        Assert.Equal(true, result.Data["servicePrincipal"]);

        // The secret appears in no value of the diagnosis.
        Assert.DoesNotContain(
            "segredo-que-nao-pode-vazar",
            string.Join('|', result.Data.Values.Select(value => value?.ToString())));
    }

    [Fact]
    public async Task TheProbeDaxDoesNotReferenceAnyTable()
    {
        // It is what makes the check work on any dataset, and not break when the model changes.
        var connection = new FakeXmlaConnection("ping");

        await Run(Check(new StubPool(connection)));

        Assert.Equal("EVALUATE ROW(\"ping\", 1)", connection.LastQuery);
        Assert.DoesNotContain("FILTER", connection.LastQuery);
    }

    [Fact]
    public async Task TheCheckRentsWithTheConfiguredConnectionString()
    {
        var pool = new StubPool(new FakeXmlaConnection("ping"));

        await Run(Check(pool));

        Assert.Equal(
            "Data Source=powerbi://api.powerbi.com/v1.0/myorg/Workspace;Catalog=Modelo;",
            Assert.Single(pool.Rentals));
    }

    // ---------- wiring ----------

    [Fact]
    public void TheCheckIsRegistrableInOneLine()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PowerBi:XmlaEndpoint"] = "powerbi://api.powerbi.com/v1.0/myorg/Workspace",
                ["PowerBi:Dataset"] = "Modelo"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddXmlaConnectionPool(configuration);
        services.AddHealthChecks().AddPowerLinqXmlaCheck(tags: "ready");

        IServiceProvider provider = services.BuildServiceProvider();
        HealthCheckServiceOptions registered =
            provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        HealthCheckRegistration registration = Assert.Single(registered.Registrations);
        Assert.Equal(XmlaHealthCheck.Name, registration.Name);
        Assert.Contains("ready", registration.Tags);
    }
}
