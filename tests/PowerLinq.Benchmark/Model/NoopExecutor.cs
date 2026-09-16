using PowerLinq.DaxConverter.Interfaces;

namespace PowerLinq.Benchmark.Model;

/// <summary>
/// An executor that never touches the network. The benchmarks measure query construction and DAX
/// generation — transport is deliberately left out of the measurement. It always returns one row
/// (from a static cache, so as not to pollute the allocations) so that <c>FirstAsync</c> has a
/// result instead of throwing.
/// </summary>
public sealed class NoopExecutor : IDaxQueryExecutor
{
    public static readonly NoopExecutor Instance = new();

    public Task<List<T>> ExecuteAsync<T>(string daxQuery, CancellationToken cancellationToken = default)
        where T : class => Task.FromResult(SingleRow<T>.Value);

    public Task<object?> ExecuteScalarAsync(string daxQuery, CancellationToken cancellationToken = default) =>
        Task.FromResult<object?>(null);

    public Task<int> ExecuteCountAsync(string daxQuery, CancellationToken cancellationToken = default)
        => Task.FromResult(1);
}
