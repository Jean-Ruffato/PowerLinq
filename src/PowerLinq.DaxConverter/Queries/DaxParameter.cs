namespace PowerLinq.DaxConverter.Queries;

/// <summary>
/// A compiled query's parameter: used in place of a value, it marks <b>where</b> the value goes
/// instead of saying what it is.
/// </summary>
/// <typeparam name="T">The type of the value this parameter receives at execution time.</typeparam>
/// <param name="Slot">The position in the list of values, zero-based.</param>
/// <remarks>
/// <para>
/// <b>It has no value, and that is the guarantee — not a limitation.</b> The central risk of
/// caching a translation: a key that treats the captured constant as part of the identity returns
/// the DAX of a different filter, a correctness bug dressed up as an optimization. Here the value
/// does not exist at the moment the DAX is produced, so it cannot be baked into the template.
/// </para>
/// <para>
/// <see cref="Value"/> and the implicit conversion <b>throw</b>. They exist so that
/// <c>p.Categoria == categoria</c> compiles with the natural syntax, and the translator recognizes
/// them <b>structurally</b> — by the expression's shape — before evaluating anything. If some path
/// it does not recognize ever executes them, the result is a loud, immediate exception, and not a
/// template with the first value written into it forever. Failing is the correct behaviour here:
/// the silent alternative is exactly the bug being avoided.
/// </para>
/// </remarks>
public sealed record DaxParameter<T>(int Slot) : IDaxParameter
{
    /// <summary>
    /// <b>Never returns.</b> It exists to give <c>T</c> the syntax of a value; see the type's
    /// remarks.
    /// </summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public T Value => throw new InvalidOperationException(NoValueMessage);

    /// <summary>
    /// <b>Never returns.</b> It allows writing <c>p.Column == parameter</c> without <c>.Value</c>;
    /// the conversion is recognized by the expression's shape and never executed in a translation.
    /// </summary>
    /// <param name="parameter">The parameter.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public static implicit operator T(DaxParameter<T> parameter) =>
        throw new InvalidOperationException(NoValueMessage);

    private const string NoValueMessage =
        "A compiled query parameter has no value: it marks where a value goes, so that the "
        + "generated DAX can be cached without baking one in. This was read outside a translation "
        + "that recognizes it — the query was probably composed directly instead of through "
        + "DaxCompiledQuery, or the parameter was used in a position the translator does not "
        + "recognize as a value.";
}
