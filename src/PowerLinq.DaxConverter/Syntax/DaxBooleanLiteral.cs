namespace PowerLinq.DaxConverter.Syntax;

/// <summary>DAX boolean, written as <c>TRUE</c> or <c>FALSE</c> — unquoted, since they are keywords.</summary>
public sealed record DaxBooleanLiteral : IDaxExpression
{
    /// <summary>The true instance. Shared: the node is immutable and carries no state.</summary>
    public static readonly DaxBooleanLiteral True = new(true);

    /// <summary>The false instance.</summary>
    public static readonly DaxBooleanLiteral False = new(false);

    private DaxBooleanLiteral(bool value) => Value = value;

    /// <summary>The value.</summary>
    public bool Value { get; }

    /// <inheritdoc/>
    public void Write(DaxWriter writer) => writer.Append(Value ? "TRUE" : "FALSE");
}
