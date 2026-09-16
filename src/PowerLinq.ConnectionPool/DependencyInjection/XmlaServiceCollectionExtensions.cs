using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PowerLinq.ConnectionPool.Executors;
using PowerLinq.ConnectionPool.Options;
using PowerLinq.DaxConverter.Interfaces;

namespace PowerLinq.Extensions.DependencyInjection;

/// <summary>
/// Registration of the XMLA executor (<see cref="XmlaQueryExecutor"/>) in the DI container.
/// </summary>
public static class XmlaServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="XmlaQueryExecutor"/> as <see cref="IDaxQueryExecutor"/> with
    /// request scope — one ADOMD connection per scope, disposed at the end. The connection string
    /// is resolved per scope from <see cref="PowerBiOptions"/>
    /// (<see cref="PowerBiOptions.BuildConnectionString"/>), so having the <c>PowerBi</c> section
    /// bound (<c>services.Configure&lt;PowerBiOptions&gt;(...)</c>) is enough.
    /// </summary>
    public static IServiceCollection AddXmlaQueryExecutor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IDaxQueryExecutor>(sp =>
        {
            PowerBiOptions options = sp.GetRequiredService<IOptions<PowerBiOptions>>().Value;

            // QueryTimeoutSeconds applies on both paths: it used to be honoured only by the pool,
            // so turning the pool off silently changed the effective timeout.
            return new XmlaQueryExecutor(options.BuildConnectionString(), options.QueryTimeoutSeconds);
        });

        return services;
    }
}
