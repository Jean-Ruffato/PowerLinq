using System.Collections.Frozen;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AnalysisServices.AdomdClient;
using PowerLinq.ConnectionPool.Options;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Mapping;

namespace PowerLinq.ConnectionPool.Executors;

/// <summary>
/// Executor working directly on a single <see cref="AdomdConnection"/>, used when the pool is
/// off. This is the rollback path: simple and obvious, not fast.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADOMD.NET is not thread-safe</b>, and this executor is registered per scope, so nothing
/// stops two parallel queries in the same scope — which is the natural pattern on a dashboard
/// (<c>Task.WhenAll</c>). Access is serialized by a <see cref="SemaphoreSlim"/>: the next query
/// waits for the previous one to finish, instead of both using the same connection at the same
/// time. Opening goes through the same gate, because testing <c>State != Open</c> and then
/// calling <c>Open()</c> is a race between concurrent calls.
/// </para>
/// <para>
/// Anyone who needs real concurrency should use the pool
/// (<c>PowerBi:ConnectionPoolEnabled = true</c>), which keeps several connections per model.
/// </para>
/// </remarks>
public sealed class XmlaQueryExecutor
    : IDaxQueryExecutor, IDaxRawQueryExecutor, IDaxStreamingQueryExecutor, IAsyncDisposable
{
    private readonly AdomdConnection _connection;
    private readonly int _commandTimeoutSeconds;

    // Serializes commands and opening: ADOMD does not support simultaneous use of one connection.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="connectionString">Full ADOMD connection string, credentials included.</param>
    /// <param name="commandTimeoutSeconds">
    /// Timeout of each command, in seconds. The default mirrors
    /// <see cref="PowerBiOptions.QueryTimeoutSeconds"/>, so turning the pool on or off does not
    /// change the effective timeout.
    /// </param>
    public XmlaQueryExecutor(string connectionString, int commandTimeoutSeconds = 120)
    {
        _connection = new AdomdConnection(connectionString);
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <inheritdoc/>
    public async Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
        where T : class
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenAsync(cancellationToken);

            using AdomdCommand command = CreateCommand(daxQuery);
            using CancellationTokenRegistration registration = RegisterCancel(command, cancellationToken);
            using AdomdDataReader reader = command.ExecuteReader();

            FrozenDictionary<string, DaxColumnMapping> mappings = EntityMapper.GetColumnMappings(typeof(T));
            var results = new List<T>();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(EntityMapper.MapRow<T>(reader, mappings));
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The same loop as <see cref="ExecuteAsync{T}"/>, with <c>yield return</c> in place of
    /// <c>Add</c> — that is literally the difference between streaming and materializing. The
    /// <c>gate</c> is released in the <c>finally</c>, which the compiler puts in the enumerator's
    /// disposal: a consumer that abandons the enumeration through <c>break</c> or an exception
    /// does not leave the connection stuck.
    /// </remarks>
    public async IAsyncEnumerable<T> ExecuteStreamAsync<T>(
        string daxQuery,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where T : class
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenAsync(cancellationToken);

            using AdomdCommand command = CreateCommand(daxQuery);
            using CancellationTokenRegistration registration = RegisterCancel(command, cancellationToken);
            using AdomdDataReader reader = command.ExecuteReader();

            FrozenDictionary<string, DaxColumnMapping> mappings = EntityMapper.GetColumnMappings(typeof(T));

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return EntityMapper.MapRow<T>(reader, mappings);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reads the cells by the name the reader declares, without mapping onto a contract: that is
    /// what <c>ToPagedListAsync</c> needs, because the total column does not belong to the
    /// contract and materialization would drop it.
    /// </remarks>
    public async Task<DaxResult> ExecuteRowsAsync(
        string daxQuery,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenAsync(cancellationToken);

            using AdomdCommand command = CreateCommand(daxQuery);
            using CancellationTokenRegistration registration = RegisterCancel(command, cancellationToken);
            using AdomdDataReader reader = command.ExecuteReader();

            string[] columns = [.. Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)];
            var rows = new List<DaxRow>();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cells = new Dictionary<string, object?>(columns.Length, StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < columns.Length; i++)
                    cells[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);

                rows.Add(new DaxRow(cells));
            }

            return new DaxResult(columns, rows);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureOpenAsync(cancellationToken);

            using AdomdCommand command = CreateCommand(daxQuery);
            using CancellationTokenRegistration registration = RegisterCancel(command, cancellationToken);
            using AdomdDataReader reader = command.ExecuteReader();

            // No row, no column or a BLANK cell all become null — the distinction between "there
            // was no row" and "the value is zero" belongs to the caller, and it is what makes
            // MinAsync throw instead of returning 0.
            return reader.Read() && reader.FieldCount > 0 && !reader.IsDBNull(0)
                ? reader.GetValue(0)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default) =>
        await ExecuteScalarAsync(daxQuery, cancellationToken) is { } value
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : 0;

    private AdomdCommand CreateCommand(string daxQuery) =>
        new(daxQuery, _connection) { CommandTimeout = _commandTimeoutSeconds };

    /// <summary>
    /// Wires the token to the command: without this the <c>CancellationToken</c> was decorative
    /// once <c>ExecuteReader</c> had started, and the query ran to completion on the server.
    /// </summary>
    private static CancellationTokenRegistration RegisterCancel(
        AdomdCommand command,
        CancellationToken cancellationToken) =>
        cancellationToken.Register(static state => ((AdomdCommand)state!).Cancel(), command);

    // ADOMD.NET has no real asynchronous open; the Task.Run keeps the caller's thread from
    // blocking. It runs under the gate, so the test/open pair does not race another call.
    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_connection.State != ConnectionState.Open)
            await Task.Run(_connection.Open, cancellationToken);
    }

    /// <summary>Closes the scope's ADOMD connection.</summary>
    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        _gate.Dispose();

        return ValueTask.CompletedTask;
    }
}
