using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ModelContextProtocol.Authentication;

namespace OpenID.MCPInterop.Client;

/// <summary>
/// Drives the two browser-based logins this Client runs: the Agent
/// Governance (CIMD) leg's authorization-code+PKCE flow (handed to the MCP
/// SDK as its <see cref="AuthorizationCallbackHandler"/>), and the EMA
/// leg's dedicated, hand-rolled one against Keycloak. Both open a browser
/// tab and wait on a <see cref="TaskCompletionSource{TResult}"/> that the
/// matching route in <see cref="Endpoints"/> completes when the loopback
/// callback lands.
/// </summary>
internal static class LoginFlows
{
    public static async Task<AuthorizationResult?> HandleAuthorizationCallbackAsync(
        AuthorizationCallbackContext context,
        ConcurrentDictionary<string, TaskCompletionSource<AuthorizationResult>> pendingAuthorizations,
        CancellationToken cancellationToken)
    {
        var authorizationQuery = QueryHelpers.ParseQuery(context.AuthorizationUri.Query);
        if (!authorizationQuery.TryGetValue("state", out var stateValues) || stateValues.Count == 0)
        {
            throw new InvalidOperationException("Authorization URI did not include a state parameter.");
        }

        var state = stateValues[0]!;
        var tcs = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingAuthorizations[state] = tcs;

        Console.WriteLine($"Opening browser for authorization: {context.AuthorizationUri}");
        Process.Start(new ProcessStartInfo(context.AuthorizationUri.ToString()) { UseShellExecute = true });

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return await tcs.Task;
    }

    // Client isn't acting as an MCP transport for the EMA leg's subject-token
    // login, so the SDK's ClientOAuthProvider (used for the CIMD leg above)
    // doesn't apply here - deliberately not reusing the CIMD login for this
    // either: that login's granted scope is resource-driven (intersected
    // against Server's protected-resource-metadata, already observed dropping
    // 'openid' from the requested scope list) and isn't guaranteed to yield an
    // id_token. This is a second, minimal hand-rolled authorization-code+PKCE
    // flow requesting 'openid' scope directly against Keycloak.
    public static async Task<string> RunEmaLoginAsync(
        HttpClient httpClient,
        string authority,
        bool requireHttpsMetadata,
        string clientId,
        string redirectUri,
        ConcurrentDictionary<string, TaskCompletionSource<AuthorizationResult>> pendingEmaAuthorizations,
        CancellationToken cancellationToken)
    {
        // Cached the same way Issuer's own subject-token validation caches
        // Keycloak's discovery document (see Issuer/Endpoints.cs), instead of
        // a raw one-off HttpClient.GetFromJsonAsync call - avoids re-fetching
        // if this flow ever runs more than once in a process's lifetime, and
        // matches the one caching pattern this codebase already established
        // rather than a second, ad hoc one. Note this doesn't (and can't,
        // from here) eliminate the *separate* discovery fetch the MCP SDK's
        // own IdentityAssertionGrantProvider does internally moments later
        // when redeeming the ID-JAG - that one happens entirely inside the
        // SDK, outside this codebase's control. RequireHttps mirrors Issuer's
        // fix for the same IDX20108 error against a local-dev, plain-http
        // Keycloak.
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

        var tcs = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingEmaAuthorizations[state] = tcs;

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

        Console.WriteLine($"Opening browser for the EMA leg's dedicated subject-token login: {authorizationUri}");
        Process.Start(new ProcessStartInfo(authorizationUri) { UseShellExecute = true });

        AuthorizationResult authorizationResult;
        using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
        {
            authorizationResult = await tcs.Task;
        }

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
