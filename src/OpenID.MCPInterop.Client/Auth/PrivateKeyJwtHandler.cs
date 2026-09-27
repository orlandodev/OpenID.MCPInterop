using Duende.IdentityModel;
using Microsoft.AspNetCore.WebUtilities;

namespace OpenID.MCPInterop.Client.Auth;

/// <summary>
/// Adds an RFC 7523 section 2.2 client_assertion to every token-endpoint
/// request the MCP SDK makes for the CIMD client, which is how a CIMD client
/// advertising <c>private_key_jwt</c> authenticates (CIMD section 8.2). MCP
/// SDK 2.0.0's <c>ClientOAuthOptions</c> only knows client_secret/none and has
/// no assertion hook, but it sends its token requests through the HttpClient
/// handed to HttpClientTransport - so this sits in that client's handler
/// chain and rewrites the form body in flight instead of reimplementing the
/// SDK's authorization-code flow.
///
/// Token requests are recognized by their body (an authorization_code or
/// refresh_token grant for this client_id), not their URL, so no AS
/// discovery is needed here; everything else passes through untouched.
/// </summary>
internal sealed class PrivateKeyJwtHandler(
    string clientId,
    ClientSigningKey signingKey,
    TimeProvider timeProvider,
    Action<string> log,
    HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    private static readonly HashSet<string> AuthenticatedGrantTypes =
    [
        OidcConstants.GrantTypes.AuthorizationCode,
        OidcConstants.GrantTypes.RefreshToken,
    ];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = await ReadTokenRequestFormAsync(request, cancellationToken);
        if (form is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var audience = request.RequestUri!.GetLeftPart(UriPartial.Path);

        form.Remove(OidcConstants.TokenRequest.ClientSecret);
        form[OidcConstants.TokenRequest.ClientAssertionType] = OidcConstants.ClientAssertionTypes.JwtBearer;
        form[OidcConstants.TokenRequest.ClientAssertion] =
            ClientAssertionFactory.Create(clientId, audience, signingKey.SigningCredentials, timeProvider);

        request.Content = new FormUrlEncodedContent(form);
        request.Headers.Authorization = null;

        log($"Attached private_key_jwt client_assertion (kid={signingKey.KeyId}, aud={audience}).");

        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<Dictionary<string, string>?> ReadTokenRequestFormAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post
            || request.RequestUri is null
            || request.Content?.Headers.ContentType?.MediaType != "application/x-www-form-urlencoded")
        {
            return null;
        }

        var body = await request.Content.ReadAsStringAsync(cancellationToken);
        var form = QueryHelpers.ParseQuery(body).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());

        var isTokenRequestForThisClient =
            form.TryGetValue(OidcConstants.TokenRequest.GrantType, out var grantType)
            && AuthenticatedGrantTypes.Contains(grantType)
            && form.TryGetValue(OidcConstants.TokenRequest.ClientId, out var formClientId)
            && formClientId == clientId;

        return isTokenRequestForThisClient ? form : null;
    }
}
