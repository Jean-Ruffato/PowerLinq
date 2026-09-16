using Microsoft.Extensions.Options;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.ConnectionPool.Options;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Localization;

namespace PowerLinq.ConnectionPool.Executors;

/// <summary>
/// Creates <see cref="PooledXmlaQueryExecutor"/> instances pointed at targets resolved at run
/// time, over the same pool.
/// </summary>
/// <remarks>
/// The pool is shared on purpose: it groups connections by connection string, and it is the target
/// that defines the string. A factory with its own pool per target would recreate the very problem
/// the pool solves — opening a connection per query — only distributed.
/// </remarks>
public sealed class PooledXmlaQueryExecutorFactory(
    IXmlaConnectionPool pool,
    IOptions<PowerBiOptions> options,
    IPowerLinqLocalizer localizer) : IDaxQueryExecutorFactory
{
    /// <inheritdoc/>
    public IDaxQueryExecutor Create(DaxTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Validate(target);

        return new PooledXmlaQueryExecutor(pool, options, target);
    }

    /// <summary>
    /// Rejects an incomplete target, naming both fields.
    /// </summary>
    /// <remarks>
    /// Without this guard, an empty workspace would produce <c>Data Source=;Catalog=X;</c> — a
    /// syntactically valid connection string that the server rejects with an authentication error,
    /// a message that says nothing about the target not being resolved. In a multi-tenant flow,
    /// "I could not resolve the customer's workspace" is the expected error and needs to look
    /// like one.
    /// </remarks>
    private void Validate(DaxTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Workspace) || string.IsNullOrWhiteSpace(target.Dataset))
        {
            // The values are quoted in the message: an empty one shows up as '' instead of
            // vanishing, which is the difference between "the target came in wrong" and "the
            // target did not come at all".
            throw new ArgumentException(
                localizer.Format("TargetIncomplete", target.Workspace, target.Dataset),
                nameof(target));
        }
    }
}
