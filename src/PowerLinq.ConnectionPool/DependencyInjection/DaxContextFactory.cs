using Microsoft.Extensions.DependencyInjection;
using PowerLinq.DaxConverter.Context;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;

namespace PowerLinq.Extensions.DependencyInjection;

/// <inheritdoc cref="IDaxContextFactory"/>
public sealed class DaxContextFactory(
    IServiceProvider provider,
    IDaxQueryExecutorFactory executorFactory) : IDaxContextFactory
{
    /// <inheritdoc/>
    public TContext Create<TContext>(DaxTarget target) where TContext : DaxContext
    {
        ArgumentNullException.ThrowIfNull(target);

        IDaxQueryExecutor executor = executorFactory.Create(target);

        // The executor goes in as an explicit argument; the rest of the context's constructor
        // (IDaxTableFactory, plus whatever the user has added) comes from the container.
        return ActivatorUtilities.CreateInstance<TContext>(provider, executor);
    }
}
