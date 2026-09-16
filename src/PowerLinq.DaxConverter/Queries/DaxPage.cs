namespace PowerLinq.DaxConverter.Queries;

/// <summary>A page of results and the row count of the whole set.</summary>
/// <typeparam name="T">The contract of each row.</typeparam>
/// <param name="Items">The page's rows, in the requested order.</param>
/// <param name="Total">
/// The total of the set <b>before</b> the window — a grid's "of N results".
/// </param>
/// <remarks>
/// <para>
/// A type of its own rather than a property on the user's contract: the total is a detail of the
/// query, not of the domain, and requiring <c>[DaxColumn("[__pl_total]")]</c> on the DTO would tie
/// it to how the library paginates. The same DTO serves the paged query and the unpaged one.
/// </para>
/// <para>
/// <c>Total</c> is a <see langword="long"/> because <c>COUNTROWS</c> over a fact table goes past
/// two billion — it is the same reason <c>LongCountAsync</c> exists alongside <c>CountAsync</c>.
/// </para>
/// </remarks>
public sealed record DaxPage<T>(IReadOnlyList<T> Items, long Total);
