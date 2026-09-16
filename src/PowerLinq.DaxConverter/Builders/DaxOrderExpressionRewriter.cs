using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Builders;

/// <summary>
/// Swaps the column references of an ordering expression for the columns the result carries after
/// a reshape.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that was recorded and left unimplemented. The pending ordering was matched
/// against the reshape's result by <b>column reference</b>, so an expression — which has several,
/// or none — had nothing to match on, and the case was refused. Refusing was conservative and
/// verifiable, but it closed off <c>OrderBy(x =&gt; key(x.Column))</c> before a <c>Select</c>:
/// exactly the list of periods where the sort key is built from text and the projection reduces to
/// a single column.
/// </para>
/// <para>
/// There is no guessing here, and that is what separates this rewrite from the one refused before:
/// each column reference is swapped for its counterpart, one by one, and <b>one</b> that the
/// reshape does not carry forward is enough for the whole thing to be refused. What is gained is
/// the case where all of them survive.
/// </para>
/// <para>
/// <b>Closed by omission.</b> The traversal enumerates the nodes it knows how to cross and refuses
/// the rest, naming what it found. That is deliberate: a new node in <c>Syntax</c> would slip past
/// a traversal that returns the unknown untouched, and the symptom would be ordering by a source
/// column the result no longer has — DAX the server rejects, or worse, DAX it accepts while
/// answering a different question.
/// </para>
/// </remarks>
internal static class DaxOrderExpressionRewriter
{
    /// <summary>
    /// Rewrites <paramref name="expression"/> by applying <paramref name="map"/> to each column
    /// reference.
    /// </summary>
    /// <param name="expression">The ordering expression, over the source columns.</param>
    /// <param name="map">
    /// Source reference to output reference; <see langword="null"/> when the reshape does not carry
    /// that column forward.
    /// </param>
    /// <remarks>
    /// It serves both sides of a reshape. Before it, the map is the renaming one — <c>Venda[Uf]</c>
    /// to <c>[Uf]</c>; after it, it is the identity over the result's set, and then rewriting and
    /// <b>validating</b> are the same traversal. They were two, and they diverged.
    /// </remarks>
    internal static Result Rewrite(IDaxExpression expression, Func<string, string?> map) =>
        expression switch
        {
            DaxColumnRef column => map(column.Reference) is { } output
                ? new Result(new DaxColumnRef(output), null, null)
                : new Result(null, column.Reference, null),

            // A literal has no column inside it, and it ends the recursion alongside the reference.
            DaxTextLiteral or DaxNumberLiteral or DaxBooleanLiteral or DaxBlankLiteral
                or DaxDateLiteral => new Result(expression, null, null),

            DaxBinary binary => RewriteBinary(binary, map),
            DaxNot not => RewriteNot(not, map),
            DaxIn @in => RewriteIn(@in, map),
            DaxFunctionCall call => RewriteCall(call, map),

            // References to a VAR and to a measure arrive here as nodes with no column inside, and
            // the temptation is to return them untouched. No: both are resolved by NAME, against
            // the DEFINE block and against the model, and neither of those is the reshape's result.
            // A measure in the ORDER BY of a SELECTCOLUMNS still collides by name with the output
            // column.
            _ => new Result(null, null, expression.ToDaxString()),
        };

    private static Result RewriteBinary(DaxBinary binary, Func<string, string?> map)
    {
        Result left = Rewrite(binary.Left, map);

        if (left.Expression is null)
            return left;

        Result right = Rewrite(binary.Right, map);

        if (right.Expression is null)
            return right;

        return new Result(
            binary with { Left = left.Expression, Right = right.Expression }, null, null);
    }

    private static Result RewriteNot(DaxNot not, Func<string, string?> map)
    {
        Result operand = Rewrite(not.Operand, map);

        return operand.Expression is null
            ? operand
            : new Result(not with { Operand = operand.Expression }, null, null);
    }

    private static Result RewriteIn(DaxIn @in, Func<string, string?> map)
    {
        Result value = Rewrite(@in.Value, map);

        if (value.Expression is null)
            return value;

        var items = new List<IDaxExpression>(@in.Items.Count);

        foreach (IDaxExpression item in @in.Items)
        {
            Result rewritten = Rewrite(item, map);

            if (rewritten.Expression is null)
                return rewritten;

            items.Add(rewritten.Expression);
        }

        return new Result(@in with { Value = value.Expression, Items = items }, null, null);
    }

    /// <remarks>
    /// The arguments are <see cref="IDaxNode"/> because a scalar function accepts a table —
    /// <c>COUNTROWS(FILTER(...))</c> is the case. A table argument is refused: it talks about a
    /// table of the model, whose shape the reshape did not change, so swapping its columns would be
    /// wrong; and leaving it untouched would order by a subquery the rewrite never checked.
    /// </remarks>
    private static Result RewriteCall(DaxFunctionCall call, Func<string, string?> map)
    {
        var arguments = new List<IDaxNode>(call.Arguments.Count);

        foreach (IDaxNode argument in call.Arguments)
        {
            if (argument is not IDaxExpression scalar)
                return new Result(null, null, argument.ToDaxString());

            Result rewritten = Rewrite(scalar, map);

            if (rewritten.Expression is null)
                return rewritten;

            arguments.Add(rewritten.Expression);
        }

        return new Result(call with { Arguments = arguments }, null, null);
    }
}
