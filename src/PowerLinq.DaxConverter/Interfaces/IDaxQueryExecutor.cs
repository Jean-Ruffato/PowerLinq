namespace PowerLinq.DaxConverter.Interfaces;

/// <summary>Sends DAX to the server and materializes the result.</summary>
/// <remarks>
/// It is the boundary between translation and transport: <c>DaxConverter</c> knows nothing about
/// ADOMD, and whoever implements this knows nothing about expression trees. See
/// <c>PooledXmlaQueryExecutor</c> for the implementation over the connection pool.
/// </remarks>
public interface IDaxQueryExecutor
{
    /// <summary>Runs the query and materializes each row into <typeparamref name="T"/>.</summary>
    Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>
    /// Runs the query and returns the <b>raw</b> value of the first column of the first row — the
    /// shape of <c>EVALUATE ROW("[Value]", ...)</c>. Returns <see langword="null"/> when there is
    /// no row, no column, or when the cell is <c>BLANK</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Raw on purpose.</b> The conversion to the type the caller asked for happens on the
    /// translation side, in <c>DaxValueConverter</c> — the same path row materialization uses.
    /// Duplicating the type table here would mean maintaining two lists of "what is supported"
    /// that would diverge the first time one of them gained a new type.
    /// </para>
    /// <para>
    /// <b><see langword="null"/> is not zero.</b> In DAX, <c>SUMX</c> and <c>MINX</c> over an
    /// empty table return <c>BLANK</c>, not <c>0</c>. Flattening that to <c>0</c> here would erase
    /// the difference between "summed to zero" and "there was no row", which is precisely what
    /// decides whether <c>MinAsync</c> throws or returns <see langword="null"/>.
    /// </para>
    /// </remarks>
    Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the query and reads the first column of the first row as an integer — the shape of
    /// <c>EVALUATE ROW("[Count]", COUNTROWS(...))</c>. An empty result returns <c>0</c>.
    /// </summary>
    /// <remarks>
    /// Kept because it is a public contract already in use. It is no longer the primitive: anyone
    /// who needs another type — <c>long</c> for a fact table above 2 billion rows, <c>decimal</c>
    /// for a sum — uses <see cref="ExecuteScalarAsync"/> instead of yet another fixed method per
    /// type.
    /// </remarks>
    Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default);
}
