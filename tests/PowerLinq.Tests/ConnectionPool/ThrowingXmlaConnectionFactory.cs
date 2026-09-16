using PowerLinq.ConnectionPool.Interfaces;

namespace PowerLinq.Tests.ConnectionPool;

/// <summary>A factory that fails while opening, to verify the semaphore slot does not leak.</summary>
internal sealed class ThrowingXmlaConnectionFactory(Exception failure) : IXmlaConnectionFactory
{
    public int Attempts { get; private set; }

    public Task<IXmlaConnection> CreateOpenConnectionAsync(
        string key,
        string connectionString,
        CancellationToken cancellationToken)
    {
        Attempts++;
        throw failure;
    }
}
