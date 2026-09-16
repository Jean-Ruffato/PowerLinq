namespace PowerLinq.DaxConverter.Syntax;

/// <summary>
/// Membership in a set of values: <c>Table[Column] IN { 1, 2, 3 }</c>.
/// </summary>
/// <remarks>
/// <para>
/// The alternative would be chaining <c>||</c>, which is what the user had to do by hand. DAX's
/// table constructor (<c>{ ... }</c>) expresses the same thing in one pass, and the engine
/// optimizes it better than a chain of comparisons.
/// </para>
/// <para>
/// An empty set is <b>not</b> representable: <c>IN { }</c> is a syntax error. Whoever builds the
/// node resolves that beforehand, returning a false literal — see the translation of
/// <c>Contains</c>.
/// </para>
/// </remarks>
public sealed record DaxIn(IDaxExpression Value, IReadOnlyList<IDaxExpression> Items)
    : IDaxExpression
{
    /// <summary>The same precedence as the comparisons, which is where <c>IN</c> sits in DAX.</summary>
    public int Precedence => DaxOperator.Equal.Precedence;

    /// <inheritdoc/>
    public void Write(DaxWriter writer)
    {
        writer.Operand(Value, Precedence).Append(" IN { ");

        for (int i = 0; i < Items.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            writer.Write(Items[i]);
        }

        writer.Append(" }");
    }
}
