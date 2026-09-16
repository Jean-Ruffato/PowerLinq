using AdomdAccessToken = Microsoft.AnalysisServices.AccessToken;

namespace PowerLinq.ConnectionPool.Providers;

internal sealed record CachedToken(AdomdAccessToken Token, DateTimeOffset ExpiresOn);
