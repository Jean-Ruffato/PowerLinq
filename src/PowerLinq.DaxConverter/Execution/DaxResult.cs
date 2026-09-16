namespace PowerLinq.DaxConverter.Execution;

/// <summary>Tabular result of a DAX query: column names + typed rows.</summary>
public sealed class DaxResult(IReadOnlyList<string> columns, IReadOnlyList<DaxRow> rows)
{
    /// <summary>The column names, in the order the server returned them.</summary>
    public IReadOnlyList<string> Columns { get; } = columns ?? throw new ArgumentNullException(nameof(columns));
    /// <summary>The result rows.</summary>
    public IReadOnlyList<DaxRow> Rows { get; } = rows ?? throw new ArgumentNullException(nameof(rows));

    /// <summary>Number of rows.</summary>
    public int RowCount => Rows.Count;

    /// <summary>Empty result — no columns and no rows.</summary>
    public static DaxResult Empty() => new([], []);
}
