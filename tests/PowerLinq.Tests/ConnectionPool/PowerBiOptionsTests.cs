using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PowerLinq.ConnectionPool.Options;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.Extensions.DependencyInjection;

namespace PowerLinq.Tests.ConnectionPool;

/// <summary>
/// The <see cref="PowerBiOptions"/> projections and the connection string <b>with</b> credentials.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PowerBiOptions.BuildXmlaConnectionString"/> — the pool's, with no credential — already
/// had coverage; <see cref="PowerBiOptions.BuildConnectionString"/>, the pool-less path's, had none
/// at all. It is precisely the one that <b>carries the secret</b>, and the shape
/// <c>User ID=app:{clientId}@{tenantId}</c> is what ADOMD demands of a service principal: getting
/// that format wrong is not a compile error, it is an authentication refusal at the endpoint.
/// </para>
/// <para>
/// The two stay separate on purpose, and the
/// <see cref="ThePoolStringNeverCarriesTheSecret"/> test is what pins that separation down.
/// </para>
/// </remarks>
public sealed class PowerBiOptionsTests
{
    private static PowerBiOptions WithServicePrincipal() => new()
    {
        XmlaEndpoint = "powerbi://api.powerbi.com/v1.0/myorg/Workspace",
        Dataset = "Modelo",
        TenantId = "tenant-id",
        ClientId = "client-id",
        ClientSecret = "segredo"
    };

    // ---------- connection string with credential ----------

    [Fact]
    public void BuildConnectionString_WithServicePrincipal_InjectsUserIdAndPassword()
    {
        Assert.Equal(
            "Data Source=powerbi://api.powerbi.com/v1.0/myorg/Workspace;Catalog=Modelo;"
            + "User ID=app:client-id@tenant-id;Password=segredo;",
            WithServicePrincipal().BuildConnectionString());
    }

    /// <summary>
    /// Without a full service principal the string comes out <b>with no</b> credential: the
    /// authentication falls back to the default, interactive chain. Emitting half a <c>User ID</c>
    /// would be worse — the endpoint would refuse with an error that hides the missing setting.
    /// </summary>
    [Theory]
    [InlineData(null, "client", "secret")]
    [InlineData("tenant", null, "secret")]
    [InlineData("tenant", "client", null)]
    [InlineData("", "client", "secret")]
    [InlineData("   ", "client", "secret")]
    public void BuildConnectionString_WithAnIncompleteServicePrincipal_OmitsCredentials(
        string? tenantId, string? clientId, string? clientSecret)
    {
        var options = new PowerBiOptions
        {
            XmlaEndpoint = "endpoint",
            Dataset = "modelo",
            TenantId = tenantId,
            ClientId = clientId,
            ClientSecret = clientSecret
        };

        Assert.False(options.HasServicePrincipal);
        Assert.Equal("Data Source=endpoint;Catalog=modelo;", options.BuildConnectionString());
    }

    /// <summary>
    /// The pool's string never carries the secret — there authentication is an AAD token on the
    /// connection, and the string is also the grouping <b>key</b>, which lands in log and metric.
    /// </summary>
    [Fact]
    public void ThePoolStringNeverCarriesTheSecret()
    {
        PowerBiOptions options = WithServicePrincipal();

        Assert.Contains("segredo", options.BuildConnectionString(), StringComparison.Ordinal);
        Assert.DoesNotContain("segredo", options.BuildXmlaConnectionString(), StringComparison.Ordinal);
    }

    // ---------- projections ----------

    [Fact]
    public void ToAuthOptions_CarriesTheCredentialAndTheRefreshMargin()
    {
        PowerBiOptions options = WithServicePrincipal();
        options.TokenRefreshMarginSeconds = 42;

        XmlaAuthOptions auth = options.ToAuthOptions();

        Assert.Equal("tenant-id", auth.TenantId);
        Assert.Equal("client-id", auth.ClientId);
        Assert.Equal("segredo", auth.ClientSecret);
        Assert.Equal(42, auth.TokenRefreshMarginSeconds);
    }

    /// <summary>
    /// A missing field becomes an empty string, not <see langword="null"/>: the consumer is the
    /// token provider, and a <c>null</c> there would throw <c>NullReferenceException</c> mid-auth.
    /// </summary>
    [Fact]
    public void ToAuthOptions_TurnsAbsentFieldsIntoEmptyStrings()
    {
        XmlaAuthOptions auth = new PowerBiOptions().ToAuthOptions();

        Assert.Equal("", auth.TenantId);
        Assert.Equal("", auth.ClientId);
        Assert.Equal("", auth.ClientSecret);
    }

    [Fact]
    public void ToConnectionPoolOptions_CarriesTheCeilingAndTheIdleTimeout()
    {
        var options = new PowerBiOptions
        {
            MaxConnectionsPerModel = 7,
            ConnectionIdleTimeoutMinutes = 3
        };

        XmlaConnectionPoolOptions pool = options.ToConnectionPoolOptions();

        Assert.Equal(7, pool.MaxConnectionsPerModel);
        Assert.Equal(3, pool.ConnectionIdleTimeoutMinutes);
    }

    // ---------- IsConfigured ----------

    [Theory]
    [InlineData("endpoint", "modelo", true)]
    [InlineData(null, "modelo", false)]
    [InlineData("endpoint", null, false)]
    [InlineData("", "modelo", false)]
    [InlineData("endpoint", "   ", false)]
    public void IsConfigured_RequiresBothEndpointAndDataset(
        string? endpoint, string? dataset, bool expected)
    {
        var options = new PowerBiOptions { XmlaEndpoint = endpoint, Dataset = dataset };

        Assert.Equal(expected, options.IsConfigured);
    }

    // ---------- registering the pool-less executor ----------

    private static ServiceProvider ProviderWithDirectExecutor(int queryTimeoutSeconds = 120)
    {
        var services = new ServiceCollection();
        services.Configure<PowerBiOptions>(options =>
        {
            options.XmlaEndpoint = "powerbi://api.powerbi.com/v1.0/myorg/Workspace";
            options.Dataset = "Modelo";
            options.QueryTimeoutSeconds = queryTimeoutSeconds;
        });
        services.AddXmlaQueryExecutor();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// <c>AddXmlaQueryExecutor</c> is the rollback path (<c>ConnectionPoolEnabled = false</c>) and had
    /// no coverage at all. What this test pins down is that the container <b>can</b> build the
    /// executor from <see cref="PowerBiOptions"/> — no network happens here, the ADOMD connection is
    /// only opened on the first query.
    /// </summary>
    /// <remarks>
    /// The disposal is asynchronous because <c>XmlaQueryExecutor</c> implements <b>only</b>
    /// <see cref="IAsyncDisposable"/>: a scope closed by a synchronous <c>Dispose()</c> throws
    /// <see cref="InvalidOperationException"/>. That is the container's default, and ASP.NET Core
    /// closes request scopes asynchronously — but whoever creates a scope by hand needs
    /// <c>await using</c>.
    /// </remarks>
    [Fact]
    public async Task AddXmlaQueryExecutor_ResolvesAScopedExecutorFromTheOptions()
    {
        await using ServiceProvider provider = ProviderWithDirectExecutor(queryTimeoutSeconds: 55);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDaxQueryExecutor>());
    }

    /// <summary>Scoped is the declared lifetime: one ADOMD connection per request.</summary>
    [Fact]
    public async Task AddXmlaQueryExecutor_RegistersOneExecutorPerScope()
    {
        await using ServiceProvider provider = ProviderWithDirectExecutor();
        await using AsyncServiceScope first = provider.CreateAsyncScope();
        await using AsyncServiceScope second = provider.CreateAsyncScope();

        IDaxQueryExecutor mesmoEscopo = first.ServiceProvider.GetRequiredService<IDaxQueryExecutor>();

        Assert.Same(mesmoEscopo, first.ServiceProvider.GetRequiredService<IDaxQueryExecutor>());
        Assert.NotSame(mesmoEscopo, second.ServiceProvider.GetRequiredService<IDaxQueryExecutor>());
    }

    /// <summary>
    /// The direct executor implements only <see cref="IAsyncDisposable"/>, and the container refuses
    /// to close a scope containing it through a synchronous <c>Dispose()</c>. It is pinned here
    /// because it is a real trap for whoever creates a scope by hand outside ASP.NET Core's pipeline.
    /// </summary>
    [Fact]
    public void ASynchronouslyDisposedScope_HoldingTheDirectExecutor_IsRefused()
    {
        ServiceProvider provider = ProviderWithDirectExecutor();
        IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IDaxQueryExecutor>();

        Assert.Throws<InvalidOperationException>(scope.Dispose);
    }

    [Fact]
    public void AddXmlaQueryExecutor_RefusesANullServiceCollection() =>
        Assert.Throws<ArgumentNullException>(
            () => XmlaServiceCollectionExtensions.AddXmlaQueryExecutor(null!));
}
