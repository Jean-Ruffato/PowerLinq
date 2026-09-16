using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PowerLinq.ConnectionPool.Exceptions;
using PowerLinq.ConnectionPool.Executors;
using PowerLinq.ConnectionPool.Options;
using PowerLinq.ConnectionPool.Pools;
using PowerLinq.DaxConverter.Execution;

namespace PowerLinq.Tests.ConnectionPool;

/// <summary>
/// The executor retries the query <b>once</b> when the session dies, and does not retry a DAX
/// error. The <c>while (true)</c>'s termination depends on the attempt counter, so both halves
/// have to be pinned down — including discarding the connection on the second failure, which did
/// not happen before and handed a dead connection back to the pool.
/// </summary>
public sealed class PooledXmlaQueryExecutorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed class Registro
    {
        public int Count { get; set; }
    }

    private static (PooledXmlaQueryExecutor Executor, XmlaConnectionPool Pool, FakeXmlaConnectionFactory Factory)
        Create(Action<int, FakeXmlaConnection>? configure = null, int maxPerKey = 2)
    {
        var factory = new FakeXmlaConnectionFactory { Configure = configure };
        var pool = new XmlaConnectionPool(
            factory,
            new XmlaConnectionPoolOptions(maxPerKey, 15),
            new ControllableTimeProvider(Start),
            NullLogger<XmlaConnectionPool>.Instance);

        var options = Options.Create(new PowerBiOptions
        {
            XmlaEndpoint = "powerbi://api.powerbi.com/v1.0/myorg/Workspace",
            Dataset = "Modelo",
            QueryTimeoutSeconds = 42
        });

        return (new PooledXmlaQueryExecutor(pool, options), pool, factory);
    }

    [Fact]
    public async Task HealthyQuery_RunsOnceAndOpensOneConnection()
    {
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, FakeXmlaConnectionFactory factory) = Create();

        int count = await executor.ExecuteCountAsync("EVALUATE ROW(\"[Count]\", 7)");

        Assert.Equal(7, count);
        Assert.Equal(1, factory.CreatedCount);
        Assert.Equal(1, factory.Created[0].Executions);

        pool.Dispose();
    }

    [Fact]
    public async Task BrokenSession_RetriesOnceWithAFreshConnection()
    {
        // Only the first connection fails; the index is captured at creation time.
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, FakeXmlaConnectionFactory factory) =
            Create(configure: (index, connection) =>
                connection.FailWith = _ => index == 0 ? BrokenSession.Create() : null);

        int count = await executor.ExecuteCountAsync("EVALUATE ROW(\"[Count]\", 7)");

        Assert.Equal(7, count);
        Assert.Equal(2, factory.CreatedCount);

        // The broken one was discarded; the healthy one stayed in the pool.
        Assert.True(factory.Created[0].IsDisposed);
        Assert.False(factory.Created[1].IsDisposed);

        pool.Dispose();
    }

    [Fact]
    public async Task BrokenSessionTwice_PropagatesInsteadOfLoopingForever()
    {
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, FakeXmlaConnectionFactory factory) =
            Create(configure: (_, connection) => connection.FailWith = _ => BrokenSession.Create());

        await Assert.ThrowsAsync<XmlaConnectionBrokenException>(
            () => executor.ExecuteCountAsync("EVALUATE ROW(\"[Count]\", 7)"));

        // Exactly two attempts, each with its own connection: no infinite loop.
        Assert.Equal(2, factory.CreatedCount);

        pool.Dispose();
    }

    [Fact]
    public async Task BrokenSessionTwice_DiscardsBothConnections()
    {
        // This is the test that exposed the defect: with the `when (attempt++ == 0)` filter, the
        // second failure did not enter the catch, MarkBroken was not called, and the dead
        // connection went back to the pool to be rented by the next query.
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, FakeXmlaConnectionFactory factory) =
            Create(configure: (_, connection) => connection.FailWith = _ => BrokenSession.Create());

        await Assert.ThrowsAsync<XmlaConnectionBrokenException>(
            () => executor.ExecuteCountAsync("EVALUATE ROW(\"[Count]\", 7)"));

        Assert.All(factory.Created, connection =>
            Assert.True(connection.IsDisposed, "conexão que acusou sessão perdida voltou ao pool"));

        pool.Dispose();
    }

    [Fact]
    public async Task BrokenSessionTwice_LeavesNoDeadConnectionForTheNextQuery()
    {
        // The practical consequence of the same defect, seen from outside: after a double failure,
        // the next query must not inherit the dead connection.
        var failing = true;
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, FakeXmlaConnectionFactory factory) =
            Create(configure: (_, connection) =>
                connection.FailWith = _ => failing ? BrokenSession.Create() : null);

        await Assert.ThrowsAsync<XmlaConnectionBrokenException>(
            () => executor.ExecuteCountAsync("EVALUATE ROW(\"[Count]\", 7)"));

        failing = false;
        int count = await executor.ExecuteCountAsync("EVALUATE ROW(\"[Count]\", 7)");

        Assert.Equal(7, count);
        Assert.Equal(3, factory.CreatedCount);

        pool.Dispose();
    }

    [Fact]
    public async Task DaxError_IsNotRetried()
    {
        // A query error is not a session failure: retrying would only waste a trip to the server.
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, FakeXmlaConnectionFactory factory) =
            Create(configure: (_, connection) =>
                connection.FailWith = _ => new InvalidOperationException("sintaxe DAX inválida"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteCountAsync("EVALUATE Tabela"));

        Assert.Equal(1, factory.CreatedCount);
        Assert.Equal(1, factory.Created[0].Executions);

        pool.Dispose();
    }

    [Fact]
    public async Task DaxError_KeepsTheConnectionUsableAndTheSlotFree()
    {
        // A DAX error leaves the session alive, so the connection goes back to the pool and is reused.
        // If the semaphore slot leaked, the second call would hang.
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, FakeXmlaConnectionFactory factory) =
            Create(
                configure: (_, connection) =>
                    connection.FailWith = _ => new InvalidOperationException("erro de DAX"),
                maxPerKey: 1);

        for (int i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => executor.ExecuteCountAsync("EVALUATE Tabela"));
        }

        Assert.Equal(1, factory.CreatedCount);
        Assert.Equal(3, factory.Created[0].Executions);
        Assert.False(factory.Created[0].IsDisposed);

        pool.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_MaterializesThroughEntityMapper()
    {
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, _) = Create();

        List<Registro> rows = await executor.ExecuteAsync<Registro>("EVALUATE ROW(\"[Count]\", 7)");

        Assert.Single(rows);
        Assert.Equal(7, rows[0].Count);

        pool.Dispose();
    }

    [Fact]
    public async Task EmptyResult_CountsAsZero()
    {
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, _) =
            Create(configure: (_, connection) => connection.Result = DaxResult.Empty());

        int count = await executor.ExecuteCountAsync("EVALUATE ROW(\"[Count]\", COUNTROWS(Vazia))");

        Assert.Equal(0, count);

        pool.Dispose();
    }

    // ---------- scalar ----------

    /// <summary>
    /// The scalar returns the <b>raw cell</b> of the first column of the first row, without going
    /// through the getters' defensive conversion: the caller needs to tell <c>BLANK</c> from zero,
    /// and <c>GetDecimal</c> would flatten both into <c>0</c>.
    /// </summary>
    [Fact]
    public async Task ExecuteScalarAsync_ReturnsTheFirstCellOfTheFirstRow()
    {
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, _) = Create(
            configure: (_, connection) => connection.Result = new DaxResult(
                ["[Total]", "[Outro]"],
                [
                    new DaxRow(new Dictionary<string, object?> { ["[Total]"] = 42m, ["[Outro]"] = 9 }),
                    new DaxRow(new Dictionary<string, object?> { ["[Total]"] = 99m, ["[Outro]"] = 1 })
                ]));

        Assert.Equal(42m, await executor.ExecuteScalarAsync("EVALUATE ROW(\"[Total]\", 42)"));

        pool.Dispose();
    }

    /// <summary>
    /// <c>BLANK</c> arrives as <see langword="null"/>, not as zero — it is what separates "summed
    /// to zero" from "there was no row", and what makes <c>MinAsync</c> throw instead of returning 0.
    /// </summary>
    [Fact]
    public async Task ExecuteScalarAsync_ReturnsNullForABlankCell()
    {
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, _) = Create(
            configure: (_, connection) => connection.Result = new DaxResult(
                ["[Total]"],
                [new DaxRow(new Dictionary<string, object?> { ["[Total]"] = null })]));

        Assert.Null(await executor.ExecuteScalarAsync("EVALUATE ROW(\"[Total]\", BLANK())"));

        pool.Dispose();
    }

    [Fact]
    public async Task ExecuteScalarAsync_ReturnsNullWhenThereIsNoRowOrNoColumn()
    {
        (PooledXmlaQueryExecutor semLinha, XmlaConnectionPool poolA, _) = Create(
            configure: (_, connection) => connection.Result = new DaxResult(["[Total]"], []));

        (PooledXmlaQueryExecutor semColuna, XmlaConnectionPool poolB, _) = Create(
            configure: (_, connection) => connection.Result = DaxResult.Empty());

        Assert.Null(await semLinha.ExecuteScalarAsync("EVALUATE Vazia"));
        Assert.Null(await semColuna.ExecuteScalarAsync("EVALUATE Vazia"));

        poolA.Dispose();
        poolB.Dispose();
    }

    /// <summary>
    /// <c>ExecuteRowsAsync</c> is the raw path: it returns the whole result, columns and every row
    /// — it is what <c>ToPagedListAsync</c> needs, because the total column does not belong to the
    /// contract and materialization would drop it.
    /// </summary>
    [Fact]
    public async Task ExecuteRowsAsync_ReturnsEveryColumnAndRow()
    {
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, _) = Create(
            configure: (_, connection) => connection.Result = new DaxResult(
                ["[Id]", "[TotalDeLinhas]"],
                [
                    new DaxRow(new Dictionary<string, object?> { ["[Id]"] = 1, ["[TotalDeLinhas]"] = 57 }),
                    new DaxRow(new Dictionary<string, object?> { ["[Id]"] = 2, ["[TotalDeLinhas]"] = 57 })
                ]));

        DaxResult result = await executor.ExecuteRowsAsync("EVALUATE Tabela");

        Assert.Equal(["[Id]", "[TotalDeLinhas]"], result.Columns);
        Assert.Equal(2, result.RowCount);
        Assert.Equal(57, result.Rows[0]["[TotalDeLinhas]"]);

        pool.Dispose();
    }

    // ---------- cancellation ----------

    /// <summary>
    /// Builds an executor whose connection cancels the token <b>during</b> execution, which is the
    /// scenario in question. With the token cancelled beforehand, the lease would fail at the
    /// pool's semaphore and the execution would never start — no connection would be created, and
    /// an assertion over the connection collection would pass empty, verifying nothing.
    /// </summary>
    private static (PooledXmlaQueryExecutor Executor, XmlaConnectionPool Pool, FakeXmlaConnectionFactory Factory)
        CreateCancellingMidQuery(CancellationTokenSource cts, int maxPerKey = 2, int cancelUntilCreation = int.MaxValue) =>
        Create(
            configure: (index, connection) =>
            {
                if (index >= cancelUntilCreation)
                    return;

                connection.ObservesCancellation = true;
                connection.OnExecuting = cts.Cancel;
            },
            maxPerKey: maxPerKey);

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, _) = CreateCancellingMidQuery(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ExecuteCountAsync("EVALUATE ROW(\"[Count]\", 7)", cts.Token));

        pool.Dispose();
    }

    [Fact]
    public async Task Cancellation_DiscardsTheConnectionInsteadOfPoolingIt()
    {
        // A conservative choice: after Cancel() the session state is indeterminate, so the
        // connection does not go back to the pool to poison the next query.
        using var cts = new CancellationTokenSource();
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, FakeXmlaConnectionFactory factory) =
            CreateCancellingMidQuery(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ExecuteCountAsync("EVALUATE Tabela", cts.Token));

        // Not empty: the execution really started, so there is a connection to inspect.
        FakeXmlaConnection used = Assert.Single(factory.Created);
        Assert.True(used.IsDisposed, "conexão cancelada voltou ao pool");

        pool.Dispose();
    }

    [Fact]
    public async Task Cancellation_IsNotRetried()
    {
        // Cancelling is the caller's decision, not a failure to recover from: a single execution.
        using var cts = new CancellationTokenSource();
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, FakeXmlaConnectionFactory factory) =
            CreateCancellingMidQuery(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ExecuteCountAsync("EVALUATE Tabela", cts.Token));

        Assert.Equal(1, factory.CreatedCount);
        Assert.Equal(1, factory.Created[0].Executions);

        pool.Dispose();
    }

    [Fact]
    public async Task Cancellation_LeavesTheSlotFreeForTheNextQuery()
    {
        // If the semaphore slot leaked on cancellation, the next query would hang at the cap of 1
        // and the test would blow up on a timeout instead of reaching the assertion.
        using var cts = new CancellationTokenSource();
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, FakeXmlaConnectionFactory factory) =
            CreateCancellingMidQuery(cts, maxPerKey: 1, cancelUntilCreation: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ExecuteCountAsync("EVALUATE Tabela", cts.Token));

        // Second query, fresh token: the first connection was discarded, so it opens another.
        int count = await executor.ExecuteCountAsync("EVALUATE ROW(\"[Count]\", 7)");

        Assert.Equal(7, count);
        Assert.Equal(2, factory.CreatedCount);

        pool.Dispose();
    }

    // ---------- timeout ----------

    [Fact]
    public async Task QueryTimeout_IsPassedToTheConnection()
    {
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, FakeXmlaConnectionFactory factory) = Create();

        await executor.ExecuteCountAsync("EVALUATE ROW(\"[Count]\", 7)");

        // The value comes from PowerBiOptions.QueryTimeoutSeconds, 42 in the helper.
        Assert.Equal(42, factory.Created[0].LastCommandTimeoutSeconds);

        pool.Dispose();
    }

    [Fact]
    public async Task ConcurrentQueries_AreSerializedByThePoolCap()
    {
        (PooledXmlaQueryExecutor executor, XmlaConnectionPool pool, FakeXmlaConnectionFactory factory) =
            Create(maxPerKey: 2);

        await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => executor.ExecuteCountAsync("EVALUATE ROW(\"[Count]\", 7)")));

        // 12 queries, at most 2 connections.
        Assert.True(factory.CreatedCount <= 2, $"abriu {factory.CreatedCount} conexões para um teto de 2");

        pool.Dispose();
    }
}
