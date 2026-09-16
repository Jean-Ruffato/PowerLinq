using System.Linq.Expressions;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.DaxConverter.Interfaces;

/// <summary>
/// The aggregations exposed to the selector of <see cref="DaxQuery{T}.Aggregate{TResult}"/> and,
/// through <see cref="IDaxGroup{T,TKey}"/>, to the selector of <c>GroupBy</c>.
/// </summary>
/// <remarks>
/// <para>
/// The methods exist only to give the expression tree its shape — they are <b>never executed</b>.
/// The translator recognizes them by name (<c>Sum</c>, <c>Count</c>, ...) and maps them onto DAX
/// functions (<c>SUM</c>/<c>SUMX</c>, <c>COUNTROWS</c>, ...).
/// </para>
/// <para>
/// That is why adding an overload is cheap: nothing here runs, and the translator matches on the
/// method name. What the overload buys is the <b>return type</b> — <c>Min</c> over a
/// <see cref="decimal"/> column returns <see cref="decimal"/>, without passing through
/// <see cref="double"/> on the way.
/// </para>
/// <para>
/// This was not cosmetic: <see cref="decimal"/> does <b>not</b> convert implicitly to
/// <see cref="double"/> in C#, so before, with only the <c>double?</c> overload, <c>Min</c> over a
/// value column required an explicit cast in the lambda — and the result came back as a
/// <see cref="double"/>, reintroducing rounding error in exactly the place where
/// <see cref="decimal"/> had been chosen to avoid it.
/// </para>
/// <para>
/// The overloads of a single aggregate share the <b>same</b> documentation on purpose: they differ
/// only in type, which the signature already states. The choice between the scalar form
/// (<c>SUM</c>) and the iterating one (<c>SUMX</c>) does not depend on the type, but on whether
/// the selector is a bare column or a computed expression.
/// </para>
/// </remarks>
public interface IDaxAggregate<T>
{
    /// <summary>Row count of the group — <c>COUNTROWS</c> of the query's table.</summary>
    long Count();

    /// <summary>
    /// Row count of <b>another</b> table of the model — <c>COUNTROWS('TOther's table')</c>.
    /// </summary>
    /// <typeparam name="TOther">Mapped entity of the table to count.</typeparam>
    /// <remarks>
    /// <para>
    /// It serves as an <b>existence gate</b>, not as a metric. <c>SUMMARIZECOLUMNS</c> drops a
    /// group when <b>all</b> extension columns come back BLANK, so counting fact rows is what
    /// prunes the dimension down to "only what has data" — the behaviour of a Power BI slicer.
    /// Pointing the <c>COUNTROWS</c> at the wrong table turns the pruning off and returns the whole
    /// dimension.
    /// </para>
    /// <para>
    /// It returns <see cref="long"/>? on purpose: <c>COUNTROWS</c> of a group with no rows is
    /// BLANK, and that is precisely what makes the pruning work. Materializing to <c>long</c> would
    /// turn BLANK into <c>0</c> and hide the effect.
    /// </para>
    /// <para>
    /// Two gates over different tables in the same projection give the <b>union</b> of the two
    /// facts: the group only falls away when both come back BLANK. A <c>Where</c> over both would
    /// be an intersection.
    /// </para>
    /// </remarks>
    long? Count<TOther>() where TOther : class;

    /// <summary>
    /// Count of distinct values — <c>DISTINCTCOUNT</c>. It requires a column: DAX has no
    /// <c>DISTINCTCOUNTX</c>, so a computed expression is refused rather than translated with
    /// different semantics.
    /// </summary>
    long CountDistinct(Func<T, object?> selector);

    /// <summary>Sum — <c>SUM</c> for a bare column, <c>SUMX</c> for a computed expression.</summary>
    double Sum(Func<T, double?> selector);

    /// <summary>Sum — <c>SUM</c> for a bare column, <c>SUMX</c> for a computed expression.</summary>
    double Sum(Func<T, double> selector);

    /// <summary>Sum — <c>SUM</c> for a bare column, <c>SUMX</c> for a computed expression.</summary>
    decimal Sum(Func<T, decimal?> selector);

    /// <summary>Sum — <c>SUM</c> for a bare column, <c>SUMX</c> for a computed expression.</summary>
    decimal Sum(Func<T, decimal> selector);

    /// <summary>Sum — <c>SUM</c> for a bare column, <c>SUMX</c> for a computed expression.</summary>
    long Sum(Func<T, long?> selector);

    /// <summary>Sum — <c>SUM</c> for a bare column, <c>SUMX</c> for a computed expression.</summary>
    long Sum(Func<T, long> selector);

    /// <summary>Sum — <c>SUM</c> for a bare column, <c>SUMX</c> for a computed expression.</summary>
    long Sum(Func<T, int?> selector);

    /// <summary>Sum — <c>SUM</c> for a bare column, <c>SUMX</c> for a computed expression.</summary>
    long Sum(Func<T, int> selector);

    /// <summary>
    /// Sum with additional predicates — <c>CALCULATE(SUM/SUMX(...), FILTER(...))</c>.
    /// </summary>
    /// <param name="selector">The column or expression to sum.</param>
    /// <param name="predicates">Predicates that restrict this aggregation only.</param>
    /// <remarks>
    /// The predicates are emitted as filter arguments of the <c>CALCULATE</c>, which is why
    /// filtering this extension column does not affect the other columns of the same projection.
    /// </remarks>
    double SumWhere(Func<T, double?> selector, params Expression<Func<T, bool>>[] predicates);

    /// <inheritdoc cref="SumWhere(Func{T,double?}, Expression{Func{T,bool}}[])"/>
    double SumWhere(Func<T, double> selector, params Expression<Func<T, bool>>[] predicates);

    /// <inheritdoc cref="SumWhere(Func{T,double?}, Expression{Func{T,bool}}[])"/>
    decimal SumWhere(Func<T, decimal?> selector, params Expression<Func<T, bool>>[] predicates);

    /// <inheritdoc cref="SumWhere(Func{T,double?}, Expression{Func{T,bool}}[])"/>
    decimal SumWhere(Func<T, decimal> selector, params Expression<Func<T, bool>>[] predicates);

    /// <inheritdoc cref="SumWhere(Func{T,double?}, Expression{Func{T,bool}}[])"/>
    long SumWhere(Func<T, long?> selector, params Expression<Func<T, bool>>[] predicates);

    /// <inheritdoc cref="SumWhere(Func{T,double?}, Expression{Func{T,bool}}[])"/>
    long SumWhere(Func<T, long> selector, params Expression<Func<T, bool>>[] predicates);

    /// <inheritdoc cref="SumWhere(Func{T,double?}, Expression{Func{T,bool}}[])"/>
    long SumWhere(Func<T, int?> selector, params Expression<Func<T, bool>>[] predicates);

    /// <inheritdoc cref="SumWhere(Func{T,double?}, Expression{Func{T,bool}}[])"/>
    long SumWhere(Func<T, int> selector, params Expression<Func<T, bool>>[] predicates);

    /// <summary>Average — <c>AVERAGE</c> for a bare column, <c>AVERAGEX</c> for an expression.</summary>
    double Average(Func<T, double?> selector);

    /// <summary>Average — <c>AVERAGE</c> for a bare column, <c>AVERAGEX</c> for an expression.</summary>
    double Average(Func<T, double> selector);

    /// <summary>Average — <c>AVERAGE</c> for a bare column, <c>AVERAGEX</c> for an expression.</summary>
    decimal Average(Func<T, decimal?> selector);

    /// <summary>Average — <c>AVERAGE</c> for a bare column, <c>AVERAGEX</c> for an expression.</summary>
    decimal Average(Func<T, decimal> selector);

    /// <summary>Average — <c>AVERAGE</c> for a bare column, <c>AVERAGEX</c> for an expression.</summary>
    double Average(Func<T, long?> selector);

    /// <summary>Average — <c>AVERAGE</c> for a bare column, <c>AVERAGEX</c> for an expression.</summary>
    double Average(Func<T, long> selector);

    /// <summary>Average — <c>AVERAGE</c> for a bare column, <c>AVERAGEX</c> for an expression.</summary>
    double Average(Func<T, int?> selector);

    /// <summary>Average — <c>AVERAGE</c> for a bare column, <c>AVERAGEX</c> for an expression.</summary>
    double Average(Func<T, int> selector);

    /// <summary>Smallest value — <c>MIN</c> for a bare column, <c>MINX</c> for an expression.</summary>
    double Min(Func<T, double?> selector);

    /// <summary>Smallest value — <c>MIN</c> for a bare column, <c>MINX</c> for an expression.</summary>
    double Min(Func<T, double> selector);

    /// <summary>Smallest value — <c>MIN</c> for a bare column, <c>MINX</c> for an expression.</summary>
    decimal Min(Func<T, decimal?> selector);

    /// <summary>Smallest value — <c>MIN</c> for a bare column, <c>MINX</c> for an expression.</summary>
    decimal Min(Func<T, decimal> selector);

    /// <summary>Smallest value — <c>MIN</c> for a bare column, <c>MINX</c> for an expression.</summary>
    long Min(Func<T, long?> selector);

    /// <summary>Smallest value — <c>MIN</c> for a bare column, <c>MINX</c> for an expression.</summary>
    long Min(Func<T, long> selector);

    /// <summary>Smallest value — <c>MIN</c> for a bare column, <c>MINX</c> for an expression.</summary>
    int Min(Func<T, int?> selector);

    /// <summary>Smallest value — <c>MIN</c> for a bare column, <c>MINX</c> for an expression.</summary>
    int Min(Func<T, int> selector);

    /// <summary><c>MIN</c> over a date — valid in DAX, and previously inexpressible.</summary>
    DateTime Min(Func<T, DateTime?> selector);

    /// <summary><c>MIN</c> over a date — valid in DAX, and previously inexpressible.</summary>
    DateTime Min(Func<T, DateTime> selector);

    /// <summary>Largest value — <c>MAX</c> for a bare column, <c>MAXX</c> for an expression.</summary>
    double Max(Func<T, double?> selector);

    /// <summary>Largest value — <c>MAX</c> for a bare column, <c>MAXX</c> for an expression.</summary>
    double Max(Func<T, double> selector);

    /// <summary>Largest value — <c>MAX</c> for a bare column, <c>MAXX</c> for an expression.</summary>
    decimal Max(Func<T, decimal?> selector);

    /// <summary>Largest value — <c>MAX</c> for a bare column, <c>MAXX</c> for an expression.</summary>
    decimal Max(Func<T, decimal> selector);

    /// <summary>Largest value — <c>MAX</c> for a bare column, <c>MAXX</c> for an expression.</summary>
    long Max(Func<T, long?> selector);

    /// <summary>Largest value — <c>MAX</c> for a bare column, <c>MAXX</c> for an expression.</summary>
    long Max(Func<T, long> selector);

    /// <summary>Largest value — <c>MAX</c> for a bare column, <c>MAXX</c> for an expression.</summary>
    int Max(Func<T, int?> selector);

    /// <summary>Largest value — <c>MAX</c> for a bare column, <c>MAXX</c> for an expression.</summary>
    int Max(Func<T, int> selector);

    /// <summary><c>MAX</c> over a date — valid in DAX, and previously inexpressible.</summary>
    DateTime Max(Func<T, DateTime?> selector);

    /// <summary><c>MAX</c> over a date — valid in DAX, and previously inexpressible.</summary>
    DateTime Max(Func<T, DateTime> selector);
}
