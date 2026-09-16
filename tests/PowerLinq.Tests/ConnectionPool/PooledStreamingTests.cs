using Microsoft.Extensions.Options;
using PowerLinq.ConnectionPool.Executors;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.ConnectionPool.Options;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Execution;

namespace PowerLinq.Tests.ConnectionPool;

/// <summary>
/// Streaming through the <b>pool</b> — the production path, and the delicate point.
/// </summary>
/// <remarks>
/// The connection is <b>rented</b>. In an <c>IAsyncEnumerable</c> the lease has to last until the
/// consumer finishes enumerating, and a consumer that abandons partway must not leave the
/// connection stuck — otherwise every interrupted request eats a connection from the pool until the
/// process restarts.
/// </remarks>
public sealed class PooledStreamingTests
{
    private sealed class Linha
    {
        [DaxColumn("[Id]")] public int Id { get; set; }
    }

    private sealed class StubPool(FakeXmlaConnection connection) : IXmlaConnectionPool
    {
        public int Rented { get; private set; }

        public int Returned { get; private set; }

        public Task<IPooledXmlaConnection> RentAsync(
            string connectionString, CancellationToken cancellationToken = default)
        {
            Rented++;
            return Task.FromResult<IPooledXmlaConnection>(new Lease(connection, () => Returned++));
        }

        private sealed class Lease(FakeXmlaConnection connection, Action onDispose) : IPooledXmlaConnection
        {
            public IXmlaConnection Connection => connection;

            public void MarkBroken() { }

            public void Dispose() => onDispose();
        }
    }

    private static PowerBiOptions Settings() => new()
    {
        XmlaEndpoint = "powerbi://api.powerbi.com/v1.0/myorg/W",
        Dataset = "D",
        QueryTimeoutSeconds = 30
    };

    private static FakeXmlaConnection ConnectionWith(int rows)
    {
        var linhas = new List<DaxRow>();
        for (int i = 0; i < rows; i++)
            linhas.Add(new DaxRow(new Dictionary<string, object?> { ["[Id]"] = i }));

        return new FakeXmlaConnection("k") { Result = new DaxResult(["[Id]"], linhas) };
    }

    private static PooledXmlaQueryExecutor Executor(StubPool pool) =>
        new(pool, Options.Create(Settings()));

    [Fact]
    public async Task TheRowsArrive_OneByOne()
    {
        FakeXmlaConnection connection = ConnectionWith(3);
        var lidas = new List<int>();

        await foreach (Linha linha in Executor(new StubPool(connection))
                           .ExecuteStreamAsync<Linha>("EVALUATE T"))
        {
            lidas.Add(linha.Id);
        }

        Assert.Equal([0, 1, 2], lidas);
    }

    /// <summary>
    /// <b>It does not materialize first.</b> Consuming two of a thousand rows does not make the
    /// connection produce the thousand — it is the property streaming exists to achieve, and what
    /// distinguishes this implementation from handing back the finished list dressed as a sequence.
    /// </summary>
    [Fact]
    public async Task ConsumingAFewRows_DoesNotProduceTheRest()
    {
        FakeXmlaConnection connection = ConnectionWith(1000);
        int lidas = 0;

        await foreach (Linha _ in Executor(new StubPool(connection))
                           .ExecuteStreamAsync<Linha>("EVALUATE T"))
        {
            if (++lidas == 2)
                break;
        }

        Assert.Equal(2, connection.StreamedRows);
    }

    /// <summary>
    /// The lease lasts the whole enumeration and is returned at the end — not when the call returns.
    /// </summary>
    [Fact]
    public async Task TheLease_IsReturnedWhenTheEnumerationEnds()
    {
        var pool = new StubPool(ConnectionWith(3));

        IAsyncEnumerator<Linha> enumerator =
            Executor(pool).ExecuteStreamAsync<Linha>("EVALUATE T").GetAsyncEnumerator();

        await enumerator.MoveNextAsync();

        Assert.Equal(1, pool.Rented);
        Assert.Equal(0, pool.Returned);

        await enumerator.DisposeAsync();

        Assert.Equal(1, pool.Returned);
    }

    /// <summary>
    /// Abandoning through a <c>break</c> returns the connection. Without that, every interrupted
    /// request would eat a connection from the pool until the process restarts.
    /// </summary>
    [Fact]
    public async Task AbandoningWithBreak_ReturnsTheLease()
    {
        FakeXmlaConnection connection = ConnectionWith(1000);
        var pool = new StubPool(connection);

        await foreach (Linha _ in Executor(pool).ExecuteStreamAsync<Linha>("EVALUATE T"))
            break;

        Assert.Equal(1, pool.Returned);
        Assert.True(connection.StreamReleased);
    }

    [Fact]
    public async Task AbandoningWithAnException_ReturnsTheLease()
    {
        FakeXmlaConnection connection = ConnectionWith(1000);
        var pool = new StubPool(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (Linha _ in Executor(pool).ExecuteStreamAsync<Linha>("EVALUATE T"))
                throw new InvalidOperationException("o consumidor falhou");
        });

        Assert.Equal(1, pool.Returned);
        Assert.True(connection.StreamReleased);
    }

    /// <summary>
    /// Cancelling during the enumeration stops the read and returns the connection — the token is
    /// observed on every row, not only at opening time.
    /// </summary>
    [Fact]
    public async Task ACancelledToken_StopsTheEnumerationAndReturnsTheLease()
    {
        FakeXmlaConnection connection = ConnectionWith(1000);
        var pool = new StubPool(connection);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (Linha linha in Executor(pool)
                               .ExecuteStreamAsync<Linha>("EVALUATE T", cancellation.Token))
            {
                if (linha.Id == 1)
                    await cancellation.CancelAsync();
            }
        });

        Assert.Equal(1, pool.Returned);
        Assert.True(connection.StreamReleased);
        Assert.True(connection.StreamedRows < 1000);
    }
}
