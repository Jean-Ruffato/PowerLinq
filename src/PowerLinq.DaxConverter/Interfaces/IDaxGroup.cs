using System.Linq.Expressions;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.DaxConverter.Interfaces;

/// <summary>
/// The logical group exposed to the selector of <see cref="DaxGroupedQuery{T,TKey}.Select{TResult}"/>.
/// It extends <see cref="IDaxAggregate{T}"/> with the group's key.
/// </summary>
/// <remarks>
/// Like the aggregations, it exists only to give the expression tree its shape — it is never
/// executed. <see cref="Key"/> becomes a grouping column in the <c>SUMMARIZECOLUMNS</c>.
/// </remarks>
public interface IDaxGroup<T, out TKey> : IDaxAggregate<T>
{
    /// <summary>The group's key.</summary>
    TKey Key { get; }

    /// <summary>
    /// A <b>model measure</b> as a result column — <c>"name", [Measure]</c> in the
    /// <c>SUMMARIZECOLUMNS</c>.
    /// </summary>
    /// <typeparam name="TValue">The value's type, for materialization.</typeparam>
    /// <param name="measure">The measure's name, with or without brackets.</param>
    /// <remarks>
    /// <para>
    /// Like the rest of <see cref="IDaxAggregate{T}"/>, it exists only to give the expression tree
    /// its shape — it is never executed.
    /// </para>
    /// <para>
    /// <b>Why a method and not an attribute.</b> The initial design had <c>[DaxMeasure]</c> on the
    /// contract for this shape, but the result of a grouping comes from a lambda: if the attribute
    /// won, whatever the lambda wrote into that property would be <b>silently ignored</b>. Here
    /// nothing is ignored, and a measure coexists with column aggregates in the same projection.
    /// </para>
    /// <para>
    /// <b>The measure is not iterated.</b> It already is the aggregation, and it goes in as
    /// <c>[Measure]</c> — not as a <c>SUMX</c> of anything. Wrapping it in an iterator would add it
    /// up once per row of the group, generating valid DAX with the wrong number.
    /// </para>
    /// </remarks>
    TValue Measure<TValue>(string measure);

    /// <summary>
    /// The measure evaluated with additional predicates, restricted to this column of the
    /// projection — <c>CALCULATE([Measure], FILTER(...))</c>.
    /// </summary>
    /// <typeparam name="TValue">The value's type, for materialization.</typeparam>
    /// <param name="measure">The measure's name, with or without brackets.</param>
    /// <param name="predicates">Predicates that restrict this measure only.</param>
    /// <remarks>
    /// Unlike <see cref="MeasureIgnoring{TValue}"/> and <see cref="MeasureKeepingOnly{TValue}"/>,
    /// the predicates <b>add</b> filter to the row's context. That lets two columns of the same
    /// projection use different scopes without breaking the query into several trips to XMLA.
    /// </remarks>
    TValue MeasureWhere<TValue>(string measure, params Expression<Func<T, bool>>[] predicates);

    /// <summary>
    /// The measure evaluated with the filter of the given columns <b>removed</b> —
    /// <c>CALCULATE([Measure], REMOVEFILTERS(column...))</c>.
    /// </summary>
    /// <typeparam name="TValue">The value's type, for materialization.</typeparam>
    /// <param name="measure">The measure's name, with or without brackets.</param>
    /// <param name="columns">
    /// The columns whose filter goes away. <b>None</b> removes the filter from the whole table.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>It is the denominator of a share-of-total.</b> In a grouping by category, each row is
    /// evaluated with that category's filter; removing that filter gives the total across all of
    /// them, in the same query and without bringing anything to the client:
    /// </para>
    /// <code>
    /// .GroupBy(v => v.Categoria)
    /// .Select(g => new Linha
    /// {
    ///     Categoria = g.Key,
    ///     Participacao = g.Measure&lt;decimal&gt;("Total Vendas")
    ///                    / g.MeasureIgnoring&lt;decimal&gt;("Total Vendas", v => v.Categoria)
    /// })
    /// </code>
    /// <para>
    /// <b>The parameter is a column selector, not a predicate</b>, and the difference is not one of
    /// style. Inside <c>CALCULATE</c> a predicate <b>applies</b> filter and a modifier
    /// <b>removes</b> it — the same syntactic position, the inverse effect. Passing
    /// <c>v =&gt; v.Uf == "SP"</c> here is refused while naming that difference, because
    /// translating it would produce DAX that says the opposite of what the author meant. To apply
    /// a filter, compose a <c>Where</c>.
    /// </para>
    /// <para>
    /// With no columns at all it emits <c>REMOVEFILTERS(Table)</c>. It does not emit
    /// <c>REMOVEFILTERS()</c>, which would remove the filter from <b>everything</b> — including
    /// tables the query never mentions.
    /// </para>
    /// </remarks>
    TValue MeasureIgnoring<TValue>(string measure, params Expression<Func<T, object?>>[] columns);

    /// <summary>
    /// The measure evaluated keeping the filter of <b>only</b> the given columns —
    /// <c>CALCULATE([Measure], ALLEXCEPT(Table, column...))</c>.
    /// </summary>
    /// <typeparam name="TValue">The value's type, for materialization.</typeparam>
    /// <param name="measure">The measure's name, with or without brackets.</param>
    /// <param name="columns">The columns whose filter <b>stays</b>. At least one.</param>
    /// <remarks>
    /// <para>
    /// The name states the intent; the generated DAX, <c>ALLEXCEPT</c>, describes the mechanics.
    /// The list is of what <b>survives</b> — the opposite of what a naive reading of
    /// <c>ALLEXCEPT</c> suggests — and that is why the API does not repeat the function's name.
    /// </para>
    /// <para>
    /// It is the partial subtotal: in a table grouped by region and category, keeping only the
    /// region's filter gives the region's total, against which each category can be compared.
    /// </para>
    /// <para>
    /// With no columns at all it would equal <see cref="MeasureIgnoring{TValue}"/> over the whole
    /// table, and it is refused: a one-argument <c>ALLEXCEPT</c> only makes whoever reads the DAX
    /// wonder what was excepted.
    /// </para>
    /// </remarks>
    TValue MeasureKeepingOnly<TValue>(string measure, params Expression<Func<T, object?>>[] columns);
}
