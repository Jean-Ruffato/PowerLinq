using AdomdAccessToken = Microsoft.AnalysisServices.AccessToken;

namespace PowerLinq.ConnectionPool.Interfaces;

/// <summary>
/// Supplies the AAD token used to authenticate XMLA connections, with caching and proactive
/// renewal. Singleton: it reuses a single credential (Azure.Identity's internal cache only works
/// when the credential is reused) and avoids a token round-trip per query.
/// </summary>
public interface IXmlaAccessTokenProvider
{
    /// <summary>A token valid for opening a connection (renewed if it is close to expiring).</summary>
    Task<AdomdAccessToken> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous version for the <c>AdomdConnection.OnAccessTokenExpired</c> callback
    /// (ADOMD.NET's synchronous <see cref="Func{T,TResult}"/>).
    /// </summary>
    AdomdAccessToken GetToken();
}
