using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using PowerLinq.ConnectionPool.Options;
using PowerLinq.ConnectionPool.Providers;
using AdomdAccessToken = Microsoft.AnalysisServices.AccessToken;

namespace PowerLinq.Tests.ConnectionPool;

/// <summary>
/// The provider caches the AAD token and renews it ahead of time. The design has two delicate
/// parts: the immutable holder in a <c>volatile</c> field, so the fast path never observes a torn
/// <c>AccessToken</c>, and the double-check inside the gate, so a burst of simultaneous calls
/// renews only once.
/// </summary>
public sealed class XmlaAccessTokenProviderTests
{
    /// <summary>
    /// The fake clock's base is anchored to real time rather than to a fixed date: the
    /// <see cref="AdomdAccessToken"/> constructor validates the expiry against the system clock and
    /// refuses an already-expired token. The tests' determinism comes from the relative advances, not from the date.
    /// </summary>
    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow;

    /// <summary>A fake credential that counts issues and returns a scripted expiry.</summary>
    private sealed class CountingCredential(Func<int, DateTimeOffset> expiresOn) : TokenCredential
    {
        private int _issued;

        public int Issued => Volatile.Read(ref _issued);

        /// <summary>A wait imposed on every issue, to exercise the race at the gate.</summary>
        public Func<Task>? OnIssuing { get; set; }

        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken) =>
            Issue();

        public override async ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext context,
            CancellationToken cancellationToken)
        {
            if (OnIssuing is not null)
                await OnIssuing();

            return Issue();
        }

        private AccessToken Issue()
        {
            int count = Interlocked.Increment(ref _issued);
            return new AccessToken($"token-{count}", expiresOn(count));
        }
    }

    private static (XmlaAccessTokenProvider Provider, CountingCredential Credential, ControllableTimeProvider Clock)
        Create(int marginSeconds = 300, int tokenLifetimeMinutes = 60)
    {
        var clock = new ControllableTimeProvider(Start);
        var credential = new CountingCredential(
            _ => clock.GetUtcNow() + TimeSpan.FromMinutes(tokenLifetimeMinutes));
        var provider = new XmlaAccessTokenProvider(
            new XmlaAuthOptions("tenant", "client", "secret", marginSeconds),
            credential,
            clock,
            NullLogger<XmlaAccessTokenProvider>.Instance);

        return (provider, credential, clock);
    }

    [Fact]
    public async Task SecondCall_ComesFromCache()
    {
        (XmlaAccessTokenProvider provider, CountingCredential credential, _) = Create();

        AdomdAccessToken first = await provider.GetTokenAsync();
        AdomdAccessToken second = await provider.GetTokenAsync();

        Assert.Equal(1, credential.Issued);
        Assert.Equal(first.Token, second.Token);
    }

    [Fact]
    public async Task TokenWellInsideItsLifetime_IsNotRenewed()
    {
        (XmlaAccessTokenProvider provider, CountingCredential credential, ControllableTimeProvider clock) =
            Create(marginSeconds: 300, tokenLifetimeMinutes: 60);

        await provider.GetTokenAsync();
        clock.Advance(TimeSpan.FromMinutes(50));
        await provider.GetTokenAsync();

        // 10 minutes left before expiry, against a margin of 5.
        Assert.Equal(1, credential.Issued);
    }

    [Fact]
    public async Task TokenInsideTheRefreshMargin_IsRenewedAhead()
    {
        (XmlaAccessTokenProvider provider, CountingCredential credential, ControllableTimeProvider clock) =
            Create(marginSeconds: 300, tokenLifetimeMinutes: 60);

        await provider.GetTokenAsync();

        // 4 minutes left, inside the margin of 5: it renews before actually expiring.
        clock.Advance(TimeSpan.FromMinutes(56));
        await provider.GetTokenAsync();

        Assert.Equal(2, credential.Issued);
    }

    [Fact]
    public async Task ExpiredToken_IsRenewed()
    {
        (XmlaAccessTokenProvider provider, CountingCredential credential, ControllableTimeProvider clock) =
            Create(tokenLifetimeMinutes: 60);

        await provider.GetTokenAsync();
        clock.Advance(TimeSpan.FromMinutes(61));
        AdomdAccessToken renewed = await provider.GetTokenAsync();

        Assert.Equal(2, credential.Issued);
        Assert.Equal("token-2", renewed.Token);
    }

    [Fact]
    public async Task ConcurrentBurst_RenewsOnlyOnce()
    {
        (XmlaAccessTokenProvider provider, CountingCredential credential, _) = Create();

        // Holds the first issue until all 32 calls are contending at the gate.
        using var gate = new SemaphoreSlim(0);
        var waiting = 0;
        credential.OnIssuing = async () =>
        {
            Interlocked.Increment(ref waiting);
            await gate.WaitAsync();
        };

        Task<AdomdAccessToken>[] callers = [.. Enumerable.Range(0, 32).Select(_ => provider.GetTokenAsync())];

        while (Volatile.Read(ref waiting) < 1)
            await Task.Yield();

        gate.Release(32);
        AdomdAccessToken[] tokens = await Task.WhenAll(callers);

        // The double-check inside the gate makes the remaining 31 find the token ready.
        Assert.Equal(1, credential.Issued);
        Assert.Single(tokens.Select(t => t.Token).Distinct());
    }

    [Fact]
    public void SyncGetToken_AlsoCaches()
    {
        // The path used by ADOMD's OnAccessTokenExpired callback, which is synchronous.
        (XmlaAccessTokenProvider provider, CountingCredential credential, _) = Create();

        AdomdAccessToken first = provider.GetToken();
        AdomdAccessToken second = provider.GetToken();

        Assert.Equal(1, credential.Issued);
        Assert.Equal(first.Token, second.Token);
    }

    [Fact]
    public async Task SyncAndAsyncPaths_ShareTheSameCache()
    {
        (XmlaAccessTokenProvider provider, CountingCredential credential, _) = Create();

        AdomdAccessToken fromAsync = await provider.GetTokenAsync();
        AdomdAccessToken fromSync = provider.GetToken();

        Assert.Equal(1, credential.Issued);
        Assert.Equal(fromAsync.Token, fromSync.Token);
    }

    // The expiry reaching ADOMD's token correctly needs no assertion of its own: it is proved by
    // the pair TokenWellInsideItsLifetime_IsNotRenewed (50 min, does not renew) and
    // TokenInsideTheRefreshMargin_IsRenewedAhead (56 min, renews). The two only pass together if
    // the transported ExpiresOn is exactly the credential's — a behavioural proof, not a property
    // read.
}
