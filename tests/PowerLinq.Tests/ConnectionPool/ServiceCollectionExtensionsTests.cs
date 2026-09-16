using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.ConnectionPool.Pools;
using PowerLinq.ConnectionPool.Providers;
using PowerLinq.Extensions.DependencyInjection;

namespace PowerLinq.Tests.ConnectionPool;

/// <summary>
/// The DI wiring had no coverage, and it is where an error would only surface when the application
/// starts up — the worst moment. These tests resolve the services for real, which also verifies
/// that the container can satisfy the constructors that now ask for a
/// <see cref="TimeProvider"/>.
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    private static IServiceProvider Build(bool poolEnabled)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PowerBi:XmlaEndpoint"] = "powerbi://api.powerbi.com/v1.0/myorg/Workspace",
                ["PowerBi:Dataset"] = "Modelo",
                ["PowerBi:TenantId"] = "tenant",
                ["PowerBi:ClientId"] = "client",
                ["PowerBi:ClientSecret"] = "secret",
                ["PowerBi:ConnectionPoolEnabled"] = poolEnabled ? "true" : "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddXmlaConnectionPool(configuration);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void PoolEnabled_ResolvesTheCachingStack()
    {
        using ServiceProvider provider = (ServiceProvider)Build(poolEnabled: true);

        // Actually resolving is the point: if the TimeProvider were not registered, the container
        // could not satisfy XmlaConnectionPool's constructor.
        Assert.IsType<XmlaConnectionPool>(provider.GetRequiredService<IXmlaConnectionPool>());
        Assert.IsType<XmlaAccessTokenProvider>(provider.GetRequiredService<IXmlaAccessTokenProvider>());
    }

    [Fact]
    public void PoolDisabled_ResolvesTheTransientRollbackStack()
    {
        using ServiceProvider provider = (ServiceProvider)Build(poolEnabled: false);

        Assert.IsType<TransientXmlaConnectionPool>(provider.GetRequiredService<IXmlaConnectionPool>());
        Assert.IsType<TransientXmlaAccessTokenProvider>(provider.GetRequiredService<IXmlaAccessTokenProvider>());
    }

    [Fact]
    public void TimeProvider_IsRegistered()
    {
        using ServiceProvider provider = (ServiceProvider)Build(poolEnabled: true);

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void HostRegisteredTimeProvider_TakesPrecedence()
    {
        // TryAdd: a host that already has its own TimeProvider (or a fake one, in an integration
        // test) must not be overwritten by the library.
        var clock = new ControllableTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PowerBi:XmlaEndpoint"] = "powerbi://x",
                ["PowerBi:Dataset"] = "Modelo"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddXmlaConnectionPool(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Pool_IsASingleton()
    {
        using ServiceProvider provider = (ServiceProvider)Build(poolEnabled: true);

        // The pool only makes sense shared: one instance per scope would cancel out the reuse.
        Assert.Same(
            provider.GetRequiredService<IXmlaConnectionPool>(),
            provider.GetRequiredService<IXmlaConnectionPool>());
    }

    [Fact]
    public void TokenProvider_IsASingleton()
    {
        using ServiceProvider provider = (ServiceProvider)Build(poolEnabled: true);

        // The same for the token: Azure.Identity's cache only counts with the credential reused.
        Assert.Same(
            provider.GetRequiredService<IXmlaAccessTokenProvider>(),
            provider.GetRequiredService<IXmlaAccessTokenProvider>());
    }

    [Fact]
    public void NullArguments_Throw()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddXmlaConnectionPool(configuration));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddXmlaConnectionPool(null!));
    }
}
