using System.Linq.Expressions;

namespace PowerLinq.DaxConverter.Queries;

/// <summary>A rollup level not yet translated: the key expression and the flag's name.</summary>
internal sealed record RollupLevelRequest(Expression Keys, string FlagName);
