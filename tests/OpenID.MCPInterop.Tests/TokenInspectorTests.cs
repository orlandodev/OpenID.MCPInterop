using System.Text;
using OpenID.MCPInterop.Client.Partner;
using Xunit;

namespace OpenID.MCPInterop.Tests;

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
        var session = new PartnerSessionState();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var token = BuildJwt(
            """{"typ":"at+jwt","alg":"RS256"}""",
            $$"""{"iss":"https://dev-example.us.auth0.com/","sub":"google-oauth2|123","aud":"https://control.11aiblockchain.com/mcp-interop","client_id":"abc123","exp":{{expiresAt.ToUnixTimeSeconds()}}}""");

        TokenInspector.LogClaims(session, token);

        var logLine = Assert.Single(session.Log);
        Assert.Contains("typ:at+jwt", logLine);
        Assert.Contains("iss:https://dev-example.us.auth0.com/", logLine);
        Assert.Contains("sub:google-oauth2|123", logLine);
        Assert.Contains("aud:\"https://control.11aiblockchain.com/mcp-interop\"", logLine);
        Assert.Contains("client_id/azp:abc123", logLine);
        Assert.Contains(expiresAt.ToString("u")[..16], logLine);
    }

    [Fact]
    public void LogClaims_ClientIdClaimAbsent_FallsBackToAzp()
    {
        // "azp" is Auth0's default-profile claim name for the client identifier.
        var session = new PartnerSessionState();
        var token = BuildJwt(
            """{"typ":"JWT"}""",
            """{"iss":"https://dev-example.us.auth0.com/","sub":"user-1","aud":"https://target.test","azp":"legacy-client-id","exp":1893456000}""");

        TokenInspector.LogClaims(session, token);

        var logLine = Assert.Single(session.Log);
        Assert.Contains("client_id/azp:legacy-client-id", logLine);
    }

    [Fact]
    public void LogClaims_ClaimsMissing_LogsMissingPlaceholders()
    {
        var session = new PartnerSessionState();
        var token = BuildJwt("{}", "{}");

        TokenInspector.LogClaims(session, token);

        var logLine = Assert.Single(session.Log);
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
        var session = new PartnerSessionState();

        TokenInspector.LogClaims(session, "opaque-token-with-no-dots");

        var logLine = Assert.Single(session.Log);
        Assert.Contains("not a JWT", logLine);
    }

    [Fact]
    public void LogClaims_MalformedBase64Segment_LogsDecodeFailureInsteadOfThrowing()
    {
        var session = new PartnerSessionState();
        var malformedToken = "not-valid-base64!!.also-not-valid!!.signature";

        var exception = Record.Exception(() => TokenInspector.LogClaims(session, malformedToken));

        Assert.Null(exception);
        var logLine = Assert.Single(session.Log);
        Assert.Contains("Could not decode access token claims", logLine);
    }

    private static string BuildJwt(string headerJson, string payloadJson) =>
        $"{Base64UrlEncode(headerJson)}.{Base64UrlEncode(payloadJson)}.{Base64UrlEncode("signature")}";

    private static string Base64UrlEncode(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
