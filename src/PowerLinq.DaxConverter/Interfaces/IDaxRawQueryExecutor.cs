using PowerLinq.DaxConverter.Execution;

namespace PowerLinq.DaxConverter.Interfaces;

/// <summary>
/// An executor that also returns the <b>raw</b> result, without materializing it onto a contract.
/// </summary>
/// <remarks>
/// <para>
/// A separate interface rather than a new member on <see cref="IDaxQueryExecutor"/>: pagination is
/// the only consumer, and forcing every existing implementation to gain a method it does not use
/// would cost more than it solves. Whoever does not implement it keeps compiling and keeps serving
/// the whole library, minus <c>ToPagedListAsync</c> — which refuses while naming the reason
/// instead of returning a page with no total.
/// </para>
/// <para>
/// The raw path exists because the page and the total arrive on the <b>same</b> row:
/// <c>ADDCOLUMNS(page, "[__pl_total]", ...)</c> stamps the count onto every row, and materializing
/// straight onto the user's contract would drop that column — it is not mapped, and should not be,
/// because it is a detail of the query and not of the domain.
/// </para>
/// </remarks>
public interface IDaxRawQueryExecutor
{
    /// <summary>Runs the query and returns the columns and rows as they came.</summary>
    Task<DaxResult> ExecuteRowsAsync(string daxQuery, CancellationToken cancellationToken = default);
}
