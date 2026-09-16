using System.Text;

namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// The DAX of a query with the values left <b>outside</b>: the constant chunks of text and, between
/// them, the parameter slot that separates them.
/// </summary>
/// <param name="Segments">
/// The constant chunks. Always one more than <paramref name="Slots"/> — the text before the first
/// parameter, between each pair, and after the last.
/// </param>
/// <param name="Slots">
/// The slots, in the order they appear in the text. The same slot may repeat: an ordering
/// expression goes into both the <c>TOPN</c> and the <c>ORDER BY</c>, and a parameter inside it is
/// written twice.
/// </param>
/// <remarks>
/// <para>
/// <b>This is the artifact worth caching.</b> It is constant — it depends on no value — so keeping
/// it cannot hand back the DAX of a different filter: what changes on every run lives outside it
/// and enters at binding time.
/// </para>
/// <para>
/// Produced by <see cref="DaxWriter.RenderTemplate"/>, which is where translation, folding and
/// writing happen — the three stages measurement showed to be the cost. A stored template pays for
/// none of the three.
/// </para>
/// </remarks>
public sealed record DaxTemplate(IReadOnlyList<string> Segments, IReadOnlyList<int> Slots)
{
    /// <summary>How many values binding requires — the highest slot plus one, or zero.</summary>
    /// <remarks>
    /// The <b>highest</b> slot, not the count of <see cref="Slots"/>: a repeated slot is written
    /// more than once and is still a single value.
    /// </remarks>
    public int ParameterCount { get; } = Slots.Count == 0 ? 0 : Slots.Max() + 1;

    /// <summary>Binds the values and returns the DAX.</summary>
    /// <param name="values">The values, in slot order.</param>
    /// <exception cref="ArgumentException">
    /// The number of values is not the one the template requires. Binding too few would leave a
    /// parameter's place empty — invalid syntax handed over as a query; binding too many is a sign
    /// the caller thinks the query has a parameter it does not have.
    /// </exception>
    public string Bind(IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count != ParameterCount)
        {
            throw new ArgumentException(
                $"This query takes {ParameterCount} parameter value(s), but {values.Count} "
                + "were given.",
                nameof(values));
        }

        if (Slots.Count == 0)
            return Segments[0];

        var builder = new StringBuilder(Segments[0]);

        for (int i = 0; i < Slots.Count; i++)
        {
            // The literal is chosen at each binding, not once per slot, because the value's type
            // decides the syntax: the same slot receiving null becomes BLANK() and receiving text
            // becomes a quoted string. Escaping belongs to the node, and it is the same path as
            // always — a `string.Replace` here would have to reimplement the quote doubling.
            builder.Append(DaxLiteral.From(values[Slots[i]]).ToDaxString())
                   .Append(Segments[i + 1]);
        }

        return builder.ToString();
    }
}
