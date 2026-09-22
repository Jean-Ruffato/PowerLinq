using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.DaxConverter.Execution;

namespace PowerLinq.Tests.ConnectionPool;

/// <summary>A fake XMLA connection: it records calls and allows scripting a failure.</summary>
internal sealed class FakeXmlaConnection(string key) : IXmlaConnection
{
    private int _executions;

    public string Key { get; } = key;

    public bool IsDisposed { get; private set; }

    public int Executions => Volatile.Read(ref _executions);

    /// <summary>When set, <see cref="ExecuteQuery"/> throws whatever the function returns.</summary>
    public Func<int, Exception?>? FailWith { get; set; }

    /// <summary>The result returned when no failure is scripted.</summary>
    public DaxResult Result { get; set; } = new(
        ["[Count]"],
        [new DaxRow(new Dictionary<string, object?> { ["[Count]"] = 7 })]);

    /// <summary>
    /// It observes the token during execution, the way the real connection now does by registering
    /// <c>command.Cancel()</c>: the cancellation arrives as an
    /// <see cref="OperationCanceledException"/>, not as success.
    /// </summary>
    public bool ObservesCancellation { get; set; }

    /// <summary>
    /// Run at the start of <see cref="ExecuteQuery"/>, before the token check. It lets the test
    /// cancel <b>during</b> execution: with the token already cancelled beforehand, the lease fails
    /// at the pool's semaphore and the execution never starts — which is not the scenario the
    /// scenario describes.
    /// </summary>
    public Action? OnExecuting { get; set; }

    /// <summary>
    /// When set, <see cref="Dispose"/> throws. The pool swallows a close failure on purpose — a
    /// connection that is already dead must not bring down eviction or shutdown.
    /// </summary>
    public Exception? DisposeFailure { get; set; }

    /// <summary>The timeout the executor asked for on the last execution.</summary>
    public int? LastCommandTimeoutSeconds { get; private set; }

    /// <summary>The DAX of the last execution.</summary>
    public string? LastQuery { get; private set; }

    /// <summary>How many rows the enumeration actually produced — what measures whether streaming happened.</summary>
    public int StreamedRows { get; private set; }

    /// <summary>Whether the enumeration was closed, by finishing or by abandonment.</summary>
    public bool StreamReleased { get; private set; }

    /// <summary>
    /// It hands the rows back lazily, the way the real connection does with the reader open. The
    /// <c>finally</c> is the analogue of the reader's <c>using</c>: it runs when the consumer
    /// finishes <b>or abandons</b>, because the compiler puts it in the iterator's disposal.
    /// </summary>
    public IEnumerable<DaxRow> StreamQuery(
        string daxQuery, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        LastCommandTimeoutSeconds = commandTimeoutSeconds;
        LastQuery = daxQuery;

        try
        {
            foreach (DaxRow row in Result.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StreamedRows++;
                yield return row;
            }
        }
        finally
        {
            StreamReleased = true;
        }
    }

    public DaxResult ExecuteQuery(string daxQuery, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        int attempt = Interlocked.Increment(ref _executions);
        LastCommandTimeoutSeconds = commandTimeoutSeconds;
        LastQuery = daxQuery;

        OnExecuting?.Invoke();

        if (ObservesCancellation)
            cancellationToken.ThrowIfCancellationRequested();

        if (FailWith?.Invoke(attempt) is { } failure)
            throw failure;

        return Result;
    }

    public void Dispose()
    {
        IsDisposed = true;

        if (DisposeFailure is { } failure)
            throw failure;
    }
}
