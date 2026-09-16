using Microsoft.AnalysisServices;
using PowerLinq.ConnectionPool.Interfaces;
using PowerLinq.DaxConverter.Localization;

namespace PowerLinq.ConnectionPool.Connections;

/// <summary>
/// Concrete factory that opens <see cref="AdomdXmlaConnection"/> with the token from
/// <see cref="IXmlaAccessTokenProvider"/> and the in-lifetime refresh callback.
/// </summary>
public sealed class AdomdXmlaConnectionFactory(
    IXmlaAccessTokenProvider tokenProvider,
    IPowerLinqLocalizer localizer)
    : IXmlaConnectionFactory
{
    /// <inheritdoc/>
    public async Task<IXmlaConnection> CreateOpenConnectionAsync(
        string key,
        string connectionString,
        CancellationToken cancellationToken)
    {
        AccessToken token = await tokenProvider.GetTokenAsync(cancellationToken);

        // ADOMD invokes the (synchronous) callback shortly before the token expires; hand back a new one.
        return AdomdXmlaConnection.Open(
            key,
            connectionString,
            token,
            _ => tokenProvider.GetToken(),
            localizer);
    }
}
