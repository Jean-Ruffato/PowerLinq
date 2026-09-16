using PowerLinq.DaxConverter.Execution;

namespace PowerLinq.DaxConverter.Interfaces;

/// <summary>
/// Creates an <see cref="IDaxQueryExecutor"/> pointed at a target resolved at run time.
/// </summary>
/// <remarks>
/// The executor registered in the container carries the configured target, fixed for the whole
/// process. This exists for when the target is only known during the request — the tenant's
/// workspace. The alternatives without it were all bad: registering one executor per customer (the
/// set is dynamic), rebuilding the DI graph per request, or abandoning the context and writing raw
/// DAX — which is exactly what the library exists to avoid.
/// </remarks>
public interface IDaxQueryExecutorFactory
{
    /// <summary>
    /// Returns an executor for <paramref name="target"/>. Connections stay grouped by target in
    /// the pool, so creating executors per request does not multiply connections.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The target has an empty workspace or dataset.
    /// </exception>
    IDaxQueryExecutor Create(DaxTarget target);
}
