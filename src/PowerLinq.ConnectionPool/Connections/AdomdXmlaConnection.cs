using System.Data;
using Microsoft.AnalysisServices;
using Microsoft.AnalysisServices.AdomdClient;
using PowerLinq.ConnectionPool.Exceptions;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.DaxConverter.Execution;
using PowerLinq.DaxConverter.Localization;

namespace PowerLinq.ConnectionPool.Connections;

/// <summary>
/// Concrete XMLA connection over ADOMD.NET. Registers <c>OnAccessTokenExpired</c> to renew the
/// AAD token within the connection's lifetime (the mechanism Microsoft recommends for long-lived
/// connections — see ADR-024), so the connection survives the token's ~1h expiry.
/// </summary>
public sealed class AdomdXmlaConnection : IXmlaConnection
{
    private readonly AdomdConnection _connection;
    private readonly IPowerLinqLocalizer _localizer;

    private AdomdXmlaConnection(
        string key,
        AdomdConnection connection,
        IPowerLinqLocalizer localizer)
    {
        Key = key;
        _connection = connection;
        _localizer = localizer;
    }

    /// <summary>This connection's pool key — endpoint and catalog.</summary>
    public string Key { get; }

    // Opens the connection synchronously (ADOMD.NET has no real async API). Kept isolated here.
    /// <summary>Opens the connection applying the AAD token and the renewal callback.</summary>
    public static AdomdXmlaConnection Open(
        string key,
        string connectionString,
        AccessToken accessToken,
        Func<AccessToken, AccessToken> onAccessTokenExpired,
        IPowerLinqLocalizer localizer)
    {
        var connection = new AdomdConnection(connectionString)
        {
            AccessToken = accessToken,
            OnAccessTokenExpired = onAccessTokenExpired
        };

        try
        {
            connection.Open();
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return new AdomdXmlaConnection(key, connection, localizer);
    }

    /// <inheritdoc/>
    public DaxResult ExecuteQuery(string daxQuery, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using AdomdCommand command = _connection.CreateCommand();
            command.CommandText = daxQuery;
            command.CommandTimeout = commandTimeoutSeconds;

            // Wires the token to the command. Without this the CancellationToken was decorative
            // once ExecuteReader had started: cancelling the HTTP request stopped nothing, the
            // query ran to completion on the server and the thread stayed blocked until the
            // CommandTimeout — 120 s by default. On a screen where the user flips filters
            // quickly, every abandoned flip kept holding one of the pool's connections.
            using CancellationTokenRegistration registration =
                cancellationToken.Register(static state => ((AdomdCommand)state!).Cancel(), command);

            using AdomdDataReader reader = command.ExecuteReader();
            return ReadResult(reader, cancellationToken);
        }
        // ADOMD signals cancellation as a failure of its own. Translate it to
        // OperationCanceledException, which is what a caller of an async API expects, instead of
        // leaking the driver's type.
        catch (AdomdException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (AdomdConnectionException ex)
        {
            // Transport/session failure — the service must discard the connection and retry once.
            throw new XmlaConnectionBrokenException(
                _localizer.Get("XmlaConnectionLost"), ex);
        }
        catch (AdomdException ex) when (_connection.State != ConnectionState.Open)
        {
            // Another ADOMD failure that left the connection unusable (for example a session moved
            // or expired on Premium): treat it as broken so a dead connection is not handed back to
            // the pool. DAX errors keep the connection Open and keep propagating as usual (no retry).
            throw new XmlaConnectionBrokenException(
                _localizer.Get("XmlaSessionInvalid"), ex);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The real streaming read: the <c>reader</c> stays open while the consumer enumerates, and
    /// <c>yield return</c> hands back each row as soon as it is read. The <c>using</c> statements
    /// close command and reader when enumeration ends — <b>including when it is abandoned</b>,
    /// because the compiler puts the iterator's cleanup in the enumerator's
    /// <c>DisposeAsync</c>/<c>Dispose</c>, which is what <c>foreach</c> calls when it leaves
    /// through a <c>break</c> or an exception.
    /// </remarks>
    public IEnumerable<DaxRow> StreamQuery(
        string daxQuery,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using AdomdCommand command = _connection.CreateCommand();
        command.CommandText = daxQuery;
        command.CommandTimeout = commandTimeoutSeconds;

        using CancellationTokenRegistration registration =
            cancellationToken.Register(static state => ((AdomdCommand)state!).Cancel(), command);

        // A `yield return` cannot appear inside a try with a catch, so failure translation lives
        // in the synchronous points that touch ADOMD: opening, advancing the reader and reading
        // the cells.
        using AdomdDataReader reader = OpenReader(command, cancellationToken);

        IReadOnlyList<string> columns = ReadColumns(reader, cancellationToken);

        while (ReadNext(reader, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return new DaxRow(ReadCells(reader, columns, cancellationToken), _localizer);
        }
    }

    /// <remarks>
    /// The same failure translations as <see cref="ExecuteQuery"/>, repeated here because the
    /// streaming path cannot wrap the whole <c>yield return</c> in a <c>try</c> with a
    /// <c>catch</c>.
    /// </remarks>
    private AdomdDataReader OpenReader(AdomdCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return command.ExecuteReader();
        }
        catch (AdomdException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (AdomdConnectionException ex)
        {
            throw new XmlaConnectionBrokenException(_localizer.Get("XmlaConnectionLost"), ex);
        }
        catch (AdomdException ex) when (_connection.State != ConnectionState.Open)
        {
            throw new XmlaConnectionBrokenException(_localizer.Get("XmlaSessionInvalid"), ex);
        }
    }

    /// <summary>Closes the ADOMD connection.</summary>
    public void Dispose() => _connection.Dispose();

    private DaxResult ReadResult(AdomdDataReader reader, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> columns = ReadColumns(reader, cancellationToken);

        var rows = new List<DaxRow>();
        while (ReadNext(reader, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new DaxRow(ReadCells(reader, columns, cancellationToken), _localizer));
        }

        return new DaxResult(columns, rows);
    }

    private IReadOnlyList<string> ReadColumns(AdomdDataReader reader, CancellationToken cancellationToken)
    {
        try
        {
            var columns = new List<string>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));

            return columns;
        }
        catch (AdomdException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (AdomdConnectionException ex)
        {
            throw ConnectionLost(ex);
        }
        catch (AdomdException ex) when (_connection.State != ConnectionState.Open)
        {
            throw SessionInvalid(ex);
        }
    }

    private bool ReadNext(AdomdDataReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return reader.Read();
        }
        catch (AdomdException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (AdomdConnectionException ex)
        {
            throw ConnectionLost(ex);
        }
        catch (AdomdException ex) when (_connection.State != ConnectionState.Open)
        {
            throw SessionInvalid(ex);
        }
    }

    private Dictionary<string, object?> ReadCells(
        AdomdDataReader reader,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken)
    {
        try
        {
            var cells = new Dictionary<string, object?>(columns.Count);
            for (int i = 0; i < columns.Count; i++)
                cells[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);

            return cells;
        }
        catch (AdomdException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (AdomdConnectionException ex)
        {
            throw ConnectionLost(ex);
        }
        catch (AdomdException ex) when (_connection.State != ConnectionState.Open)
        {
            throw SessionInvalid(ex);
        }
    }

    private XmlaConnectionBrokenException ConnectionLost(AdomdConnectionException ex) =>
        new(_localizer.Get("XmlaConnectionLost"), ex);

    private XmlaConnectionBrokenException SessionInvalid(AdomdException ex) =>
        new(_localizer.Get("XmlaSessionInvalid"), ex);
}
