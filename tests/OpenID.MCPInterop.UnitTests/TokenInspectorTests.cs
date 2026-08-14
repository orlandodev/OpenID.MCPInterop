using System.Text;
using OpenID.MCPInterop.Client;
using Xunit;

namespace OpenID.MCPInterop.UnitTests;

/// <summary>
/// TokenInspector had no prior coverage. Also exercises the client_id lookup
/// (now via OAuthConstants.ClientIdClaim) and its "azp" fallback through
/// observable behavior.
/// </summary>
public sealed class TokenInspectorTests
{
    [Fact]
    public void LogClaims_JwtWithAllClaims_LogsEachClaim()
    {
        var session = new ClientSessionState();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var token = BuildJwt(
            """{"typ":"at+jwt","alg":"RS256"}""",
            $$"""{"iss":"https://dev-example.us.auth0.com/","sub":"google-oauth2|123","aud":"https://control.11aiblockchain.com/mcp-interop","client_id":"abc123","exp":{{expiresAt.ToUnixTimeSeconds()}}}""");

        var claims = TokenInspector.LogClaims(session, token);

        var logLine = Assert.Single(session.Log).Message;
        Assert.Contains("typ:at+jwt", logLine);
        Assert.Contains("iss:https://dev-example.us.auth0.com/", logLine);
        Assert.Contains("sub:google-oauth2|123", logLine);
        Assert.Contains("aud:\"https://control.11aiblockchain.com/mcp-interop\"", logLine);
        Assert.Contains("client_id/azp:abc123", logLine);
        Assert.Contains(expiresAt.ToString("u")[..16], logLine);

        Assert.NotNull(claims);
        Assert.Equal("at+jwt", claims.Typ);
        Assert.Equal("https://dev-example.us.auth0.com/", claims.Iss);
        Assert.Equal("google-oauth2|123", claims.Sub);
        Assert.Equal("\"https://control.11aiblockchain.com/mcp-interop\"", claims.Aud);
        Assert.Equal("abc123", claims.ClientId);
        Assert.Equal(expiresAt.ToUnixTimeSeconds(), claims.ExpiresAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void LogClaims_ClientIdClaimAbsent_FallsBackToAzp()
    {
        // "azp" is Auth0's default-profile claim name for the client identifier.
        var session = new ClientSessionState();
        var token = BuildJwt(
            """{"typ":"JWT"}""",
            """{"iss":"https://dev-example.us.auth0.com/","sub":"user-1","aud":"https://target.test","azp":"legacy-client-id","exp":1893456000}""");

        TokenInspector.LogClaims(session, token);

        var logLine = Assert.Single(session.Log).Message;
        Assert.Contains("client_id/azp:legacy-client-id", logLine);
    }

    [Fact]
    public void LogClaims_ClaimsMissing_LogsMissingPlaceholders()
    {
        var session = new ClientSessionState();
        var token = BuildJwt("{}", "{}");

        TokenInspector.LogClaims(session, token);

        var logLine = Assert.Single(session.Log).Message;
        Assert.Contains("typ:(none)", logLine);
        Assert.Contains("iss:(missing)", logLine);
        Assert.Contains("sub:(missing)", logLine);
        Assert.Contains("aud:(missing)", logLine);
        Assert.Contains("client_id/azp:(missing)", logLine);
        Assert.Contains("exp:(missing)", logLine);
    }

    [Fact]
    public void LogClaims_OpaqueToken_LogsNotAJwtMessage()
    {
        var session = new ClientSessionState();

        var claims = TokenInspector.LogClaims(session, "opaque-token-with-no-dots");

        Assert.Null(claims);
        var logLine = Assert.Single(session.Log).Message;
        Assert.Contains("not a JWT", logLine);
    }

    [Fact]
    public void LogClaims_MalformedBase64Segment_LogsDecodeFailureInsteadOfThrowing()
    {
        var session = new ClientSessionState();
        var malformedToken = "not-valid-base64!!.also-not-valid!!.signature";

        TokenClaims? claims = null;
        var exception = Record.Exception(() => claims = TokenInspector.LogClaims(session, malformedToken));

        Assert.Null(exception);
        Assert.Null(claims);
        var logLine = Assert.Single(session.Log).Message;
        Assert.Contains("Could not decode access token claims", logLine);
    }

    private static string BuildJwt(string headerJson, string payloadJson) =>
        $"{Base64UrlEncode(headerJson)}.{Base64UrlEncode(payloadJson)}.{Base64UrlEncode("signature")}";

    private static string Base64UrlEncode(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
