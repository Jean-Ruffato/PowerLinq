using PowerLinq.DaxConverter.Localization;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.DaxConverter.Interfaces;

/// <inheritdoc cref="IDaxTableFactory"/>
public sealed class DaxTableFactory(IPowerLinqLocalizer localizer) : IDaxTableFactory
{
    /// <inheritdoc/>
    public IDaxTable<T> Create<T>(IDaxQueryExecutor executor) where T : class =>
        new DaxTable<T>(executor, localizer);
}
