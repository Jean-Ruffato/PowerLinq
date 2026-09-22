using System.Diagnostics.CodeAnalysis;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.ConnectionPool.Options;
using AdomdAccessToken = Microsoft.AnalysisServices.AccessToken;

namespace PowerLinq.ConnectionPool.Providers;

/// <summary>
/// Caches the service principal (or Managed Identity) AAD token used by the XMLA connections,
/// renewing it proactively with the <see cref="XmlaAuthOptions.TokenRefreshMarginSeconds"/> margin.
/// Registered as a singleton — it reuses a single <see cref="TokenCredential"/> (the condition for
/// Azure.Identity's internal cache to be worth anything) and removes the per-query token round-trip.
/// </summary>
public sealed class XmlaAccessTokenProvider : IXmlaAccessTokenProvider
{
    // Fixed scope of the Power BI XMLA endpoint.
    private static readonly string[] Scopes = ["https://analysis.windows.net/powerbi/api/.default"];

    private readonly XmlaAuthOptions _options;
    private readonly ILogger<XmlaAccessTokenProvider> _logger;
    private readonly TokenCredential _credential;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    // Immutable holder behind a reference: reading/writing the reference is atomic, so the fast
    // path (outside the gate) never observes a torn value (AccessToken is a multi-field struct).
    private volatile CachedToken? _cached;

    /// <summary>
    /// Derives the credential from <paramref name="options"/>: service principal when
    /// <see cref="XmlaAuthOptions.ClientSecret"/> is present, otherwise
    /// <see cref="DefaultAzureCredential"/>.
    /// </summary>
    public XmlaAccessTokenProvider(
        XmlaAuthOptions options,
        TimeProvider timeProvider,
        ILogger<XmlaAccessTokenProvider> logger)
        : this(options, CreateCredential(options), timeProvider, logger) { }

    /// <summary>
    /// Takes a ready-made credential, for when the default chain does not fit — for example a
    /// <see cref="DefaultAzureCredential"/> with its own options, or a test credential.
    /// </summary>
    public XmlaAccessTokenProvider(
        XmlaAuthOptions options,
        TokenCredential credential,
        TimeProvider timeProvider,
        ILogger<XmlaAccessTokenProvider> logger)
    {
        _options = options;
        _logger = logger;
        _credential = credential;
        _timeProvider = timeProvider;
    }

    private static TokenCredential CreateCredential(XmlaAuthOptions options) =>
        string.IsNullOrEmpty(options.ClientSecret)
            ? new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = options.TenantId })
            : new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);

    /// <inheritdoc/>
    public async Task<AdomdAccessToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetValid(out CachedToken? current))
            return current.Token;

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetValid(out current))
                return current.Token;

            AccessToken token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
            return Store(token).Token;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Synchronous version, required by ADOMD's in-lifetime renewal callback, which is not async.
    /// </summary>
    public AdomdAccessToken GetToken()
    {
        if (TryGetValid(out CachedToken? current))
            return current.Token;

        _refreshGate.Wait();
        try
        {
            if (TryGetValid(out current))
                return current.Token;

            AccessToken token = _credential.GetToken(new TokenRequestContext(Scopes), CancellationToken.None);
            return Store(token).Token;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private CachedToken Store(AccessToken azureToken)
    {
        var entry = new CachedToken(
            new AdomdAccessToken(azureToken.Token, azureToken.ExpiresOn),
            azureToken.ExpiresOn);

        _cached = entry;
        _logger.LogDebug("Token AAD do XMLA renovado (expira em {ExpiresOn:o}).", azureToken.ExpiresOn);
        return entry;
    }

    private bool TryGetValid([NotNullWhen(true)] out CachedToken? entry)
    {
        // Atomic read of the volatile reference.
        entry = _cached;
        return entry is not null
            && entry.ExpiresOn - _timeProvider.GetUtcNow()
                > TimeSpan.FromSeconds(_options.TokenRefreshMarginSeconds);
    }
}
