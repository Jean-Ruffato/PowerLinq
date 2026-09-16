namespace PowerLinq.DaxConverter.Queries;

/// <summary>Limits the number of rows — <c>TOPN(n, source, order)</c>.</summary>
/// <param name="Count">How many rows to keep.</param>
public sealed record DaxTakeStage(int Count) : DaxStage;
