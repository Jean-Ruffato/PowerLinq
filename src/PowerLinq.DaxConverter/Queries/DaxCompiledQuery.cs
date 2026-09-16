using PowerLinq.DaxConverter.Interfaces;

namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// Compiles a query once and reuses its DAX, binding the values on every execution.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape comes from measurement.</b> Splitting the three stages of producing DAX —
/// translating the lambda, folding the pipeline into a tree, writing the tree as text —
/// translation is 43% of the path with one filter and 63% with four, and it is the only one that
/// scales with the number of operators. A compiled query pays none of the three from the second
/// execution onwards.
/// </para>
/// <para>
/// <b>Why the parameter is declared rather than discovered.</b> The initial design proposed a cache
/// key that treated the captured constant as a parameter, and named the risk: getting it wrong
/// means returning the DAX of a different filter, a correctness bug dressed up as an optimization.
/// Here there is no key to get right — the parameter is a <see cref="DaxParameter{T}"/>, a marker
/// <b>with no value</b>, and the value only exists at binding time. The class of bug stops being
/// unlikely and becomes inexpressible.
/// </para>
/// <para>
/// <b>And why there is no cache dictionary.</b> The template lives in the compiled query itself,
/// which the caller keeps — typically in a <c>static readonly</c> field. There is no cache table
/// growing inside the library, so there is no expiry policy to define and no cap to configure: an
/// uncapped cache inside a library is a memory leak in the consumer's process, and the way not to
/// have that problem is not to have the table. Not using it is the off switch.
/// </para>
/// <para>
/// <b>What is not parameterizable.</b> Only what reaches the DAX as a <b>value</b>. <c>Take</c> and
/// <c>Skip</c> take an <c>int</c> and do not go through the translator, so they change the query's
/// shape and call for one compiled query per window size — a column name and an operator, for the
/// same reason, are not values.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// private static readonly DaxCompiledQuery&lt;Produto, string&gt; PorCategoria =
///     DaxCompiledQuery.Create&lt;Produto, string&gt;(
///         (tabela, categoria) => tabela.Where(p => p.Categoria == categoria));
///
/// List&lt;Produto&gt; linhas = await context.FromCompiledAsync(PorCategoria, "Eletrônicos");
/// </code>
/// </example>
public static class DaxCompiledQuery
{
    /// <summary>Compiles a query with one parameter.</summary>
    /// <typeparam name="T">The table's entity.</typeparam>
    /// <typeparam name="TParam">The type of the parameter's value.</typeparam>
    /// <param name="build">
    /// How the query is composed. It receives the table and the parameter's marker, and the marker
    /// is used where the value would go: <c>p.Categoria == categoria</c>.
    /// </param>
    public static DaxCompiledQuery<T, TParam> Create<T, TParam>(
        Func<IDaxTable<T>, DaxParameter<TParam>, DaxQuery<T>> build) where T : class =>
        new(table => build(table, new DaxParameter<TParam>(0)), parameterCount: 1);

    /// <summary>Compiles a query with two parameters.</summary>
    /// <typeparam name="T">The table's entity.</typeparam>
    /// <typeparam name="TParam1">The type of the first parameter's value.</typeparam>
    /// <typeparam name="TParam2">The type of the second parameter's value.</typeparam>
    /// <param name="build">How the query is composed.</param>
    public static DaxCompiledQuery<T, TParam1, TParam2> Create<T, TParam1, TParam2>(
        Func<IDaxTable<T>, DaxParameter<TParam1>, DaxParameter<TParam2>, DaxQuery<T>> build)
        where T : class =>
        new(table => build(table, new DaxParameter<TParam1>(0), new DaxParameter<TParam2>(1)),
            parameterCount: 2);
}
