namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// A single-column row, used internally to materialize a list of values.
/// </summary>
/// <typeparam name="TValue">The column's type.</typeparam>
/// <remarks>
/// <para>
/// It is the contract <see cref="DaxQuery{T}.DistinctValuesAsync{TValue}"/> exists to spare the
/// library's users from. It is still needed here because
/// <see cref="Interfaces.IDaxQueryExecutor.ExecuteAsync{T}"/> materializes into objects, and the
/// only alternative would be adding a transport method just to read one column — more public
/// surface for the same result.
/// </para>
/// <para>
/// <b>No <c>[DaxColumn]</c>, on purpose.</b> In the attribute's absence the mapping registers the
/// property under <c>Value</c> <b>and</b> <c>[Value]</c>, and the DAX result brings the extension
/// column in brackets — both forms match, with no need to declare which.
/// </para>
/// </remarks>
internal sealed class DaxValueRow<TValue>
{
    /// <summary>The column's value.</summary>
    public TValue Value { get; set; } = default!;
}
