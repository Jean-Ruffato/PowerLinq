using System.Runtime.CompilerServices;
using PowerLinq.DaxConverter.Attributes;
using PowerLinq.DaxConverter.Interfaces;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.Tests.Query;

/// <summary>
/// A result delivered <b>row by row</b>, without materializing the list.
/// </summary>
/// <remarks>
/// <para>
/// The way of reading was never the problem: the executor already reads through an
/// <c>AdomdDataReader</c>. What materialized everything was the loop draining the reader into a
/// list — streaming is swapping the <c>Add</c> for a <c>yield return</c>.
/// </para>
/// <para>
/// The delicate point is <b>abandonment</b>: a consumer that leaves the enumeration partway must
/// not leave the connection stuck. What guarantees that is <c>await foreach</c>, calling the
/// enumerator's <c>DisposeAsync</c> — and that is what the tests here verify.
/// </para>
/// </remarks>
public sealed class DaxStreamingTests
{
    [DaxTable("Produto")]
    private sealed class Produto
    {
        [DaxColumn("Produto[Id]")] public int Id { get; set; }
        [DaxColumn("Produto[Ativo]")] public bool Ativo { get; set; }
    }

    /// <summary>
    /// An executor that counts how many rows were <b>read</b> and whether the read was closed. It
    /// is the analogue of a reader: whoever does not enumerate to the end does not make the server produce the rest.
    /// </summary>
    private sealed class StreamingExecutor(int total) : IDaxQueryExecutor, IDaxStreamingQueryExecutor
    {
        public string? LastQuery { get; private set; }

        public int Produced { get; private set; }

        public bool Released { get; private set; }

        public async IAsyncEnumerable<T> ExecuteStreamAsync<T>(
            string daxQuery,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where T : class
        {
            LastQuery = daxQuery;

            try
            {
                for (int i = 0; i < total; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Produced++;

                    await Task.Yield();
                    yield return (T)(object)new Produto { Id = i };
                }
            }
            finally
            {
                // The analogue of the rented connection's `using`: the compiler puts this in the
                // enumerator's disposal, so it runs even when the consumer abandons the enumeration.
                Released = true;
            }
        }

        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class
        {
            LastQuery = daxQuery;
            return Task.FromResult(new List<T>());
        }

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class PlainExecutor : IDaxQueryExecutor
    {
        public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
            where T : class => Task.FromResult(new List<T>());

        public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    [Fact]
    public async Task TheRowsArrive_OneByOne()
    {
        var executor = new StreamingExecutor(3);
        var lidas = new List<int>();

        await foreach (Produto produto in new DaxTable<Produto>(executor).AsAsyncEnumerable())
            lidas.Add(produto.Id);

        Assert.Equal([0, 1, 2], lidas);
    }

    /// <summary>The query sent is the same one <c>ToListAsync</c> would send.</summary>
    [Fact]
    public async Task TheQuery_IsTheSameOneTheListPathWouldSend()
    {
        var executor = new StreamingExecutor(1);

        await foreach (Produto _ in new DaxTable<Produto>(executor)
                           .Where(p => p.Ativo)
                           .AsAsyncEnumerable())
        {
            break;
        }

        string flat = string.Join(
            ' ', executor.LastQuery!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Replace("( ", "(", StringComparison.Ordinal)
            .Replace(" )", ")", StringComparison.Ordinal);

        Assert.Equal("EVALUATE FILTER(Produto, Produto[Ativo])", flat);
    }

    /// <summary>
    /// <b>It does not materialize first.</b> Consuming only the first two of a thousand rows does
    /// not make the read produce the thousand — it is the property streaming exists to achieve.
    /// </summary>
    [Fact]
    public async Task ConsumingAFewRows_DoesNotProduceTheRest()
    {
        var executor = new StreamingExecutor(1000);
        var lidas = new List<int>();

        await foreach (Produto produto in new DaxTable<Produto>(executor).AsAsyncEnumerable())
        {
            lidas.Add(produto.Id);

            if (lidas.Count == 2)
                break;
        }

        Assert.Equal(2, lidas.Count);
        Assert.Equal(2, executor.Produced);
    }

    /// <summary>
    /// Abandoning through a <c>break</c> releases what the read was holding — on the pool path, the
    /// rented connection. Without that, a consumer stopping partway would hold a connection per request.
    /// </summary>
    [Fact]
    public async Task AbandoningWithBreak_ReleasesTheReader()
    {
        var executor = new StreamingExecutor(1000);

        await foreach (Produto _ in new DaxTable<Produto>(executor).AsAsyncEnumerable())
            break;

        Assert.True(executor.Released);
    }

    /// <summary>And abandoning through an <b>exception</b> does too.</summary>
    [Fact]
    public async Task AbandoningWithAnException_ReleasesTheReader()
    {
        var executor = new StreamingExecutor(1000);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (Produto _ in new DaxTable<Produto>(executor).AsAsyncEnumerable())
                throw new InvalidOperationException("o consumidor falhou");
        });

        Assert.True(executor.Released);
    }

    /// <summary>Enumerating to the end releases it just the same.</summary>
    [Fact]
    public async Task EnumeratingToTheEnd_ReleasesTheReader()
    {
        var executor = new StreamingExecutor(3);

        await foreach (Produto _ in new DaxTable<Produto>(executor).AsAsyncEnumerable())
        {
        }

        Assert.True(executor.Released);
    }

    [Fact]
    public async Task ACancelledToken_StopsTheEnumeration()
    {
        var executor = new StreamingExecutor(1000);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (Produto produto in new DaxTable<Produto>(executor)
                               .AsAsyncEnumerable(cancellation.Token))
            {
                if (produto.Id == 1)
                    await cancellation.CancelAsync();
            }
        });

        Assert.True(executor.Released);
    }

    /// <summary>
    /// An executor without the capability is refused while naming the reason, instead of silently
    /// materializing and handing back the list dressed as a sequence.
    /// </summary>
    [Fact]
    public void AnExecutorWithoutTheStreamingCapability_IsRefusedNamingIt()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => new DaxTable<Produto>(new PlainExecutor()).AsAsyncEnumerable());

        Assert.Contains("IDaxStreamingQueryExecutor", ex.Message, StringComparison.Ordinal);
    }
}
