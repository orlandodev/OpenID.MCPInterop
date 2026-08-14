using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpenID.MCPInterop.Common.Constants;
using OpenID.MCPInterop.Issuer;
using OpenID.MCPInterop.IntegrationTests.Support;
using Xunit;

namespace OpenID.MCPInterop.IntegrationTests;

/// <summary>
/// Exercises Issuer's real RFC 8693 /token endpoint (Endpoints.cs) end to
/// end - form parsing, the client_id allowlist, subject_token signature/issuer/
/// expiry validation, and the minted ID-JAG's claim shape - against an
/// in-process TestServer and a fake IdP handler, with no live Keycloak
/// involved. IdentityProviderAuthority and ResourceAuthority are set to
/// different values throughout (see docs/architecture.md's "Picking and
/// choosing services") specifically to catch the aud claim regressing back
/// to the identity provider's URL instead of the resource AS's.
/// </summary>
public sealed class IssuerEndpointsTests
{
    private const string ResourceAuthority = "https://resource-as.test";
    private const string IssuerUrl = "https://issuer.test";
    private const string TrustedClientId = "test-client";

    private static IssuerOptions CreateOptions(string[]? trustedClientIds = null) => new()
    {
        Url = IssuerUrl,
        IdentityProviderAuthority = FakeIdentityProviderHandler.Issuer,
        ResourceAuthority = ResourceAuthority,
        RequireHttpsMetadata = true,
        TrustedClientIds = trustedClientIds ?? [TrustedClientId],
    };

    private static Task<HttpResponseMessage> PostTokenAsync(HttpClient client, Dictionary<string, string> form) =>
        client.PostAsync("/token", new FormUrlEncodedContent(form));

    [Fact]
    public async Task Token_UnsupportedGrantType_Returns400()
    {
        using var idp = new FakeIdentityProviderHandler();
        await using var host = await IssuerTestHost.StartAsync(CreateOptions(), idp);

        var response = await PostTokenAsync(host.Client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("unsupported_grant_type", body!.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Token_MissingRequiredFields_Returns400InvalidRequest()
    {
        using var idp = new FakeIdentityProviderHandler();
        await using var host = await IssuerTestHost.StartAsync(CreateOptions(), idp);

        var response = await PostTokenAsync(host.Client, new Dictionary<string, string>
        {
            ["grant_type"] = OAuthConstants.TokenExchangeGrantType,
            // subject_token, client_id, and resource all omitted.
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("invalid_request", body!.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Token_NonFormContentType_Returns400InvalidRequest()
    {
        using var idp = new FakeIdentityProviderHandler();
        await using var host = await IssuerTestHost.StartAsync(CreateOptions(), idp);

        var response = await host.Client.PostAsJsonAsync("/token", new { grant_type = OAuthConstants.TokenExchangeGrantType });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("invalid_request", body!.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Token_UntrustedClientId_Returns400InvalidClient()
    {
        using var idp = new FakeIdentityProviderHandler();
        await using var host = await IssuerTestHost.StartAsync(CreateOptions(trustedClientIds: [TrustedClientId]), idp);

        var response = await PostTokenAsync(host.Client, new Dictionary<string, string>
        {
            ["grant_type"] = OAuthConstants.TokenExchangeGrantType,
            ["subject_token"] = "irrelevant-not-validated-before-the-client-check",
            ["client_id"] = "some-other-client",
            ["resource"] = "https://target-mcp-server.test/mcp",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("invalid_client", body!.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Token_MalformedSubjectToken_Returns400InvalidGrant()
    {
        using var idp = new FakeIdentityProviderHandler();
        await using var host = await IssuerTestHost.StartAsync(CreateOptions(), idp);

        var response = await PostTokenAsync(host.Client, new Dictionary<string, string>
        {
            ["grant_type"] = OAuthConstants.TokenExchangeGrantType,
            ["subject_token"] = "not-a-jwt",
            ["client_id"] = TrustedClientId,
            ["resource"] = "https://target-mcp-server.test/mcp",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("invalid_grant", body!.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Token_ExpiredSubjectToken_Returns400InvalidGrant()
    {
        using var idp = new FakeIdentityProviderHandler();
        await using var host = await IssuerTestHost.StartAsync(CreateOptions(), idp);

        var expiredIdToken = idp.CreateIdToken("test-subject", expiresIn: TimeSpan.FromMinutes(-5));

        var response = await PostTokenAsync(host.Client, new Dictionary<string, string>
        {
            ["grant_type"] = OAuthConstants.TokenExchangeGrantType,
            ["subject_token"] = expiredIdToken,
            ["client_id"] = TrustedClientId,
            ["resource"] = "https://target-mcp-server.test/mcp",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("invalid_grant", body!.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Token_SubjectTokenFromWrongIssuer_Returns400InvalidGrant()
    {
        using var idp = new FakeIdentityProviderHandler();
        await using var host = await IssuerTestHost.StartAsync(CreateOptions(), idp);

        // Correctly signed by the fake IdP's own key, but claiming an issuer
        // that doesn't match what its own discovery document advertises -
        // e.g. a token from a different tenant/realm of the same IdP software.
        var wrongIssuerIdToken = idp.CreateIdToken("test-subject", issuer: "https://not-the-configured-idp.test");

        var response = await PostTokenAsync(host.Client, new Dictionary<string, string>
        {
            ["grant_type"] = OAuthConstants.TokenExchangeGrantType,
            ["subject_token"] = wrongIssuerIdToken,
            ["client_id"] = TrustedClientId,
            ["resource"] = "https://target-mcp-server.test/mcp",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("invalid_grant", body!.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Token_ValidSubjectToken_ReturnsIdJagAudiencedAtResourceAuthority()
    {
        using var idp = new FakeIdentityProviderHandler();
        var options = CreateOptions();
        await using var host = await IssuerTestHost.StartAsync(options, idp);

        var idToken = idp.CreateIdToken("test-subject");
        const string resource = "https://target-mcp-server.test/mcp";

        var response = await PostTokenAsync(host.Client, new Dictionary<string, string>
        {
            ["grant_type"] = OAuthConstants.TokenExchangeGrantType,
            ["subject_token"] = idToken,
            ["client_id"] = TrustedClientId,
            ["resource"] = resource,
            ["scope"] = "mcp:tools extra-scope",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(OAuthConstants.IdJagTokenType, body!.RootElement.GetProperty("issued_token_type").GetString());
        Assert.Equal("N_A", body.RootElement.GetProperty("token_type").GetString());

        var idJag = new JwtSecurityTokenHandler().ReadJwtToken(body.RootElement.GetProperty("access_token").GetString());

        Assert.Equal(OAuthConstants.IdJagHeaderTyp, idJag.Header.Typ);
        Assert.Equal(options.Url, idJag.Issuer);
        Assert.Equal("test-subject", idJag.Subject);
        Assert.Equal(TrustedClientId, idJag.Claims.Single(c => c.Type == OAuthConstants.ClientIdClaim).Value);
        Assert.Equal(resource, idJag.Claims.Single(c => c.Type == OAuthConstants.ResourceClaim).Value);
        Assert.Equal("mcp:tools extra-scope", idJag.Claims.Single(c => c.Type == OAuthConstants.ScopeClaim).Value);
        Assert.False(string.IsNullOrEmpty(idJag.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Jti).Value));

        // The regression this test exists to catch: the aud claim must be the
        // resource AS Client will redeem this ID-JAG at, not the identity
        // provider that issued the subject_token - those are deliberately
        // different values in this test (see options above).
        Assert.Contains(ResourceAuthority, idJag.Audiences);
        Assert.DoesNotContain(FakeIdentityProviderHandler.Issuer, idJag.Audiences);

        // Short-lived per IdJagClaims.ExpiresAt's doc comment - minutes, not hours.
        Assert.True(idJag.ValidTo - idJag.ValidFrom <= TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WellKnownJwks_PublishesIssuersOwnSigningKey()
    {
        using var idp = new FakeIdentityProviderHandler();
        await using var host = await IssuerTestHost.StartAsync(CreateOptions(), idp);

        var response = await host.Client.GetAsync("/.well-known/jwks.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var keys = body!.RootElement.GetProperty("keys");
        Assert.Equal(1, keys.GetArrayLength());
        var key = keys[0];
        Assert.Equal("sig", key.GetProperty("use").GetString());
        Assert.Equal("RS256", key.GetProperty("alg").GetString());
        Assert.False(string.IsNullOrEmpty(key.GetProperty("kid").GetString()));
        Assert.False(string.IsNullOrEmpty(key.GetProperty("n").GetString()));
    }

    [Fact]
    public async Task WellKnownOpenIdConfiguration_MatchesIssuerOptions()
    {
        using var idp = new FakeIdentityProviderHandler();
        var options = CreateOptions();
        await using var host = await IssuerTestHost.StartAsync(options, idp);

        var response = await host.Client.GetAsync("/.well-known/openid-configuration");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(options.Url, body!.RootElement.GetProperty("issuer").GetString());
        Assert.Equal($"{options.Url}/token", body.RootElement.GetProperty("token_endpoint").GetString());
        Assert.Equal($"{options.Url}/.well-known/jwks.json", body.RootElement.GetProperty("jwks_uri").GetString());
        Assert.Contains(
            OAuthConstants.TokenExchangeGrantType,
            body.RootElement.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString()));
    }
}
