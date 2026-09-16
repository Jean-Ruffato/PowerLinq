using PowerLinq.ConnectionPool.Providers;

namespace PowerLinq.ConnectionPool.Options;

/// <summary>
/// AAD credentials + token renewal policy used to authenticate the XMLA connections
/// (<see cref="XmlaAccessTokenProvider"/> / <see cref="TransientXmlaAccessTokenProvider"/>). These
/// are the library's own values — the host maps its configuration (appsettings, for example) onto
/// this record when registering the token providers. If <see cref="ClientSecret"/> comes in empty,
/// DefaultAzureCredential is used (Managed Identity / the standard Azure chain).
/// </summary>
/// <param name="TenantId">Azure AD / Entra ID tenant.</param>
/// <param name="ClientId">Application (client) ID of the service principal.</param>
/// <param name="ClientSecret">Service principal secret; empty => DefaultAzureCredential.</param>
/// <param name="TokenRefreshMarginSeconds">Margin (s) before expiry within which the token is renewed proactively.</param>
public sealed record XmlaAuthOptions(
    string TenantId,
    string ClientId,
    string ClientSecret,
    int TokenRefreshMarginSeconds = 300);
