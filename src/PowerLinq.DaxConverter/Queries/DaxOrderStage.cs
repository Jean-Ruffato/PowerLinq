using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// One ordering term. <c>ThenBy</c> adds another stage instead of growing this one.
/// </summary>
/// <param name="Term">The column and the direction.</param>
/// <param name="ResetsOrder">
/// <see langword="true"/> for <c>OrderBy</c>/<c>OrderByDescending</c>, which <b>replace</b> the
/// accumulated ordering; <see langword="false"/> for <c>ThenBy</c>/<c>ThenByDescending</c>, which
/// add a term.
/// </param>
/// <remarks>
/// <para>
/// One term per stage, and not a list per stage, because that is how composition happens:
/// <c>OrderBy(a).ThenBy(b)</c> is two calls. Consecutive ordering stages are consumed together by
/// the next window.
/// </para>
/// <para>
/// <b><see cref="ResetsOrder"/> is LINQ semantics, not convenience.</b> There,
/// <c>OrderBy(a).OrderBy(b)</c> sorts by <c>b</c> alone — the second <c>OrderBy</c> reorders the
/// whole sequence; the one that adds a criterion is <c>ThenBy</c>. The distinction did not exist in
/// the flat definition, which had only a list of terms and appended in both cases, and that is why
/// such a chain produced <c>ORDER BY a, b</c>. It was wrong, but unreachable in practice because
/// ordering after ordering without <c>ThenBy</c> had no use — and it would become reachable now
/// that ordering after a window is allowed.
/// </para>
/// </remarks>
public sealed record DaxOrderStage(DaxOrderTerm Term, bool ResetsOrder) : DaxStage;
