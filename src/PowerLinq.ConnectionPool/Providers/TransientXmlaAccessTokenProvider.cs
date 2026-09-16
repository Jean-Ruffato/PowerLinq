using Azure.Core;
using Azure.Identity;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.ConnectionPool.Options;
using AdomdAccessToken = Microsoft.AnalysisServices.AccessToken;

namespace PowerLinq.ConnectionPool.Providers;

/// <summary>
/// Implementation of <see cref="IXmlaAccessTokenProvider"/> used when the pool is OFF
/// (<c>PowerBi:ConnectionPoolEnabled = false</c>). Reproduces the legacy behaviour (pre-ADR-024):
/// creates a credential and fetches a fresh AAD token on every call, with no caching.
/// </summary>
public sealed class TransientXmlaAccessTokenProvider(XmlaAuthOptions options)
    : IXmlaAccessTokenProvider
{
    private static readonly string[] Scopes = ["https://analysis.windows.net/powerbi/api/.default"];

    private readonly XmlaAuthOptions _options = options;

    /// <inheritdoc/>
    public async Task<AdomdAccessToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        AccessToken token = await CreateCredential().GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        return new AdomdAccessToken(token.Token, token.ExpiresOn);
    }

    /// <summary>Synchronous version, required by the ADOMD token renewal callback.</summary>
    public AdomdAccessToken GetToken()
    {
        AccessToken token = CreateCredential().GetToken(new TokenRequestContext(Scopes), CancellationToken.None);
        return new AdomdAccessToken(token.Token, token.ExpiresOn);
    }

    private TokenCredential CreateCredential() =>
        string.IsNullOrEmpty(_options.ClientSecret)
            ? new DefaultAzureCredential(new DefaultAzureCredentialOptions { TenantId = _options.TenantId })
            : new ClientSecretCredential(_options.TenantId, _options.ClientId, _options.ClientSecret);
}
