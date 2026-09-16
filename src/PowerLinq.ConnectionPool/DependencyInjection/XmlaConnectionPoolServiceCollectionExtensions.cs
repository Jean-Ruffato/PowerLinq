using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PowerLinq.ConnectionPool.Connections;
using PowerLinq.ConnectionPool.Executors;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.ConnectionPool.Options;
using PowerLinq.ConnectionPool.Pools;
using PowerLinq.ConnectionPool.Providers;
using PowerLinq.DaxConverter.Interfaces;

namespace PowerLinq.Extensions.DependencyInjection;

/// <summary>
/// Registration of the XMLA connection pool from a single <see cref="PowerBiOptions"/> (the
/// <c>PowerBi</c> section). The app's options object is the source of truth: it projects
/// <see cref="XmlaAuthOptions"/> and <see cref="XmlaConnectionPoolOptions"/> onto the library's
/// internal layers.
/// </summary>
public static class XmlaConnectionPoolServiceCollectionExtensions
{
    /// <summary>
    /// Binds the <c>PowerBi</c> section to <see cref="PowerBiOptions"/> and registers the XMLA
    /// connection stack: token provider, connection factory and pool. The
    /// <see cref="PowerBiOptions.ConnectionPoolEnabled"/> flag (read at registration time) chooses
    /// between the singleton pool + cached token (on) and the legacy transient mode (off — the
    /// rollback).
    /// </summary>
    public static IServiceCollection AddXmlaConnectionPool(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddPowerLinqLocalization(configuration);

        // Source of time for the pool and for the token cache. TryAdd so a host that already
        // registers its own TimeProvider (or a fake one, in an integration test) wins.
        services.TryAddSingleton(TimeProvider.System);

        IConfigurationSection section = configuration.GetSection(PowerBiOptions.SectionName);
        services.Configure<PowerBiOptions>(section);

        // The flag decides WHICH implementations to register, so it needs the value now (not through IOptions).
        bool poolEnabled = section.GetValue("ConnectionPoolEnabled", true);

        // PowerBiOptions projects the library's own options — consumed in both modes.
        services.AddSingleton(sp =>
            sp.GetRequiredService<IOptions<PowerBiOptions>>().Value.ToAuthOptions());

        // The connection factory is the same in both modes (it uses the registered token provider).
        services.AddSingleton<IXmlaConnectionFactory, AdomdXmlaConnectionFactory>();

        if (poolEnabled)
        {
            services.AddSingleton<IXmlaAccessTokenProvider, XmlaAccessTokenProvider>();
            services.AddSingleton(sp =>
                sp.GetRequiredService<IOptions<PowerBiOptions>>().Value.ToConnectionPoolOptions());
            services.AddSingleton<IXmlaConnectionPool, XmlaConnectionPool>();
        }
        else
        {
            services.AddSingleton<IXmlaAccessTokenProvider, TransientXmlaAccessTokenProvider>();
            services.AddSingleton<IXmlaConnectionPool, TransientXmlaConnectionPool>();
        }

        // Executor for a target resolved at run time. It applies in both modes, because both
        // expose IXmlaConnectionPool and it is the connection string that carries the target.
        services.TryAddSingleton<IDaxQueryExecutorFactory, PooledXmlaQueryExecutorFactory>();

        return services;
    }
}
