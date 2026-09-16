using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.ConnectionPool.Pools;

namespace PowerLinq.Tests.ConnectionPool;

/// <summary>
/// The transient pool is the rollback path (<c>PowerBi:ConnectionPoolEnabled = false</c>): it opens
/// one connection per lease and closes it on return, with no reuse, cap or eviction. It had no
/// coverage precisely because it is plan B — the worst place to keep a surprise.
/// </summary>
public sealed class TransientXmlaConnectionPoolTests
{
    private const string Key = "Data Source=powerbi://a;Catalog=Modelo;";

    [Fact]
    public async Task EveryRent_OpensANewConnection()
    {
        var factory = new FakeXmlaConnectionFactory();
        var pool = new TransientXmlaConnectionPool(factory);

        using (IPooledXmlaConnection first = await pool.RentAsync(Key))
        using (IPooledXmlaConnection second = await pool.RentAsync(Key))
        {
            Assert.NotSame(first.Connection, second.Connection);
        }

        Assert.Equal(2, factory.CreatedCount);
    }

    [Fact]
    public async Task Dispose_ClosesTheConnectionInsteadOfPoolingIt()
    {
        var factory = new FakeXmlaConnectionFactory();
        var pool = new TransientXmlaConnectionPool(factory);

        FakeXmlaConnection connection;
        using (IPooledXmlaConnection lease = await pool.RentAsync(Key))
        {
            connection = (FakeXmlaConnection)lease.Connection;
            Assert.False(connection.IsDisposed);
        }

        Assert.True(connection.IsDisposed);
    }

    [Fact]
    public async Task NoCap_ManySimultaneousRentsAllSucceed()
    {
        // Unlike the capped pool, here 16 simultaneous leases wait for nobody.
        var factory = new FakeXmlaConnectionFactory();
        var pool = new TransientXmlaConnectionPool(factory);

        IPooledXmlaConnection[] leases = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => pool.RentAsync(Key)));

        Assert.Equal(16, factory.CreatedCount);

        foreach (IPooledXmlaConnection lease in leases)
            lease.Dispose();

        Assert.All(factory.Created, connection => Assert.True(connection.IsDisposed));
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var factory = new FakeXmlaConnectionFactory();
        var pool = new TransientXmlaConnectionPool(factory);

        IPooledXmlaConnection lease = await pool.RentAsync(Key);
        lease.Dispose();
        lease.Dispose();

        Assert.Single(factory.Created);
        Assert.True(factory.Created[0].IsDisposed);
    }

    [Fact]
    public async Task MarkBroken_IsANoOpBecauseNothingIsPooled()
    {
        var factory = new FakeXmlaConnectionFactory();
        var pool = new TransientXmlaConnectionPool(factory);

        using (IPooledXmlaConnection lease = await pool.RentAsync(Key))
        {
            lease.MarkBroken();
        }

        // Marked broken or not, the connection is closed — there is no stack to protect.
        Assert.True(factory.Created[0].IsDisposed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RentWithBlankKey_Throws(string key)
    {
        var pool = new TransientXmlaConnectionPool(new FakeXmlaConnectionFactory());

        await Assert.ThrowsAsync<ArgumentException>(() => pool.RentAsync(key));
    }
}
