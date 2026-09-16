namespace PowerLinq.DaxConverter.Queries;

/// <summary>Discards the first rows — <c>EXCEPT(source, TOPN(n, source, order))</c>.</summary>
/// <param name="Count">How many rows to discard.</param>
public sealed record DaxSkipStage(int Count) : DaxStage;
