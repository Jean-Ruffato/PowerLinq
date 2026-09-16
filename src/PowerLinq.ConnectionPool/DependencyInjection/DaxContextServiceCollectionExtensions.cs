using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PowerLinq.DaxConverter.Context;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;

namespace PowerLinq.Extensions.DependencyInjection;

/// <summary>Registration of the DAX context and the table factory in the container.</summary>
public static class DaxContextServiceCollectionExtensions
{
    /// <summary>
    /// Registers the table factory and one DAX context per scope. The context must take
    /// IDaxQueryExecutor and IDaxTableFactory in its constructor.
    /// </summary>
    public static IServiceCollection AddDaxContext<TContext>(
        this IServiceCollection services,
        IConfiguration? configuration = null)
        where TContext : DaxContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPowerLinqLocalization(configuration);
        services.AddSingleton<IDaxTableFactory, DaxTableFactory>();
        services.AddScoped<TContext>();

        // Explicit target (multi-tenant). Only the context factory is registered here, because it
        // is transport-agnostic; the one that knows how to build an executor is the connection
        // package, and it is the one that registers IDaxQueryExecutorFactory in
        // AddXmlaConnectionPool.
        services.TryAddScoped<IDaxContextFactory, DaxContextFactory>();
        return services;
    }

    internal static IServiceCollection AddPowerLinqLocalization(
        this IServiceCollection services,
        IConfiguration? configuration)
    {
        OptionsBuilder<PowerLinqLocalizationOptions> options = services.AddOptions<PowerLinqLocalizationOptions>();
        if (configuration is not null)
            options.Bind(configuration.GetSection(PowerLinqLocalizationOptions.SectionName));

        services.TryAddSingleton<IPowerLinqLocalizer>(provider =>
            new ResourceManagerPowerLinqLocalizer(
                provider.GetRequiredService<IOptions<PowerLinqLocalizationOptions>>().Value.Language));
        return services;
    }
}
