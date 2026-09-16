namespace PowerLinq.DaxConverter.Syntax;

/// <summary>A date, as the <c>DATE(year,month,day)</c> function.</summary>
public sealed record DaxDateLiteral(int Year, int Month, int Day) : IDaxExpression
{
    /// <inheritdoc/>
    public void Write(DaxWriter writer) =>
        writer.Append("DATE(")
              .Append(Year).Append(',')
              .Append(Month).Append(',')
              .Append(Day)
              .Append(')');
}
