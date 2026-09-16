using PowerLinq.DaxConverter.Syntax;

namespace PowerLinq.DaxConverter.Translators;

/// <summary>An aggregation's extension column: the name and the DAX expression.</summary>
public sealed record DaxAggregateColumn(string Name, IDaxExpression Expression);
