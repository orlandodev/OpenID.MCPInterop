using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ModelContextProtocol.Authentication;
using OpenID.MCPInterop.Client;
using OpenID.MCPInterop.Client.State;

namespace OpenID.MCPInterop.Client.Auth;

/// <summary>
/// Drives the browser-based logins this Client runs: the primary leg's
/// (CIMD or direct-trust, depending on ClientOptions.UseCimd) authorization-
/// code+PKCE flow, handed to the MCP SDK as its
/// <see cref="ClientOAuthOptions.AuthorizationCallbackHandler"/>, and the
/// EMA leg's dedicated, hand-rolled one against Keycloak. Both redirect the
/// browser that clicked "Connect"/"Start EMA leg" (via a
/// <see cref="TaskCompletionSource{TResult}"/> of <see cref="Uri"/> the
/// matching /connect or /ema-connect route awaits) rather than opening a new
/// tab, then wait on a state-keyed <see cref="TaskCompletionSource{TResult}"/>
/// of <see cref="AuthorizationResult"/> that the matching /callback or
/// /ema-callback route in <see cref="Endpoints"/> completes.
/// </summary>
internal static class LoginFlows
{
    private static async Task<AuthorizationResult> WaitForCallbackAsync(
        Uri authorizationUri,
        ConcurrentDictionary<string, TaskCompletionSource<AuthorizationResult>> pendingCallbacks,
        TaskCompletionSource<Uri> authorizationUriReady,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var authorizationQuery = QueryHelpers.ParseQuery(authorizationUri.Query);
        if (!authorizationQuery.TryGetValue("state", out var stateValues) || stateValues.Count == 0)
        {
            throw new InvalidOperationException("Authorization URI did not include a state parameter.");
        }

        var state = stateValues[0]!;
        var tcs = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingCallbacks[state] = tcs;

        log($"Authorization server: {authorizationUri.GetLeftPart(UriPartial.Authority)}");
        authorizationUriReady.TrySetResult(authorizationUri);

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return await tcs.Task;
    }

    public static async Task<AuthorizationResult?> HandleAuthorizationCallbackAsync(
        AuthorizationCallbackContext context,
        ClientSessionState session,
        TaskCompletionSource<Uri> authorizationUriReady,
        CancellationToken cancellationToken) =>
        await WaitForCallbackAsync(context.AuthorizationUri, session.PendingCallbacks, authorizationUriReady, session.AppendLog, cancellationToken);

    // Client isn't acting as an MCP transport for the EMA leg's subject-token
    // login, so the primary leg's ClientOAuthOptions don't apply here -
    // deliberately not reusing the primary leg's login for this either: that
    // login's granted scope is resource-driven (intersected against Server's
    // protected-resource-metadata, already observed dropping 'openid' from
    // the requested scope list) and isn't guaranteed to yield an id_token.
    // This is a second, minimal hand-rolled authorization-code+PKCE flow
    // requesting 'openid' scope directly against Keycloak.
    public static async Task<string> RunEmaLoginAsync(
        HttpClient httpClient,
        string authority,
        bool requireHttpsMetadata,
        string clientId,
        string redirectUri,
        ClientSessionState session,
        TaskCompletionSource<Uri> emaAuthorizationUriReady,
        CancellationToken cancellationToken)
    {
        var documentRetriever = new HttpDocumentRetriever(httpClient) { RequireHttps = requireHttpsMetadata };
        var configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{authority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            documentRetriever);
        var discovery = await configManager.GetConfigurationAsync(cancellationToken);
        var authorizationEndpoint = discovery.AuthorizationEndpoint
            ?? throw new InvalidOperationException("Keycloak discovery document did not include an authorization_endpoint.");
        var tokenEndpoint = discovery.TokenEndpoint
            ?? throw new InvalidOperationException("Keycloak discovery document did not include a token_endpoint.");

        var codeVerifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var state = Guid.NewGuid().ToString("N");

        var authorizationUri = QueryHelpers.AddQueryString(authorizationEndpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        });

        var authorizationResult = await WaitForCallbackAsync(
            new Uri(authorizationUri), session.PendingEmaCallbacks, emaAuthorizationUriReady, session.AppendLog, cancellationToken);

        using var tokenRequest = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationResult.Code ?? throw new InvalidOperationException("Authorization callback did not include a code."),
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier,
        });
        using var tokenResponse = await httpClient.PostAsync(tokenEndpoint, tokenRequest, cancellationToken);
        tokenResponse.EnsureSuccessStatusCode();

        var tokenJson = await tokenResponse.Content.ReadFromJsonAsync<JsonNode>(cancellationToken)
            ?? throw new InvalidOperationException("Keycloak token response was empty.");
        return tokenJson["id_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException(
                "Keycloak token response did not include an id_token - confirm the EMA login client requests the 'openid' scope.");
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
