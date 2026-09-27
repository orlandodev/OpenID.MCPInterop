using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenID.MCPInterop.Client.Auth;
using OpenID.MCPInterop.UnitTests.Support;
using Xunit;

namespace OpenID.MCPInterop.UnitTests;

public sealed class PrivateKeyJwtHandlerTests
{
    private const string ClientId = "https://client.dev.internal:5050/client-metadata.json";
    private const string TokenEndpoint = "http://localhost:8080/realms/mcpinterop/protocol/openid-connect/token";

    [Theory]
    [InlineData("authorization_code")]
    [InlineData("refresh_token")]
    public async Task TokenRequest_ShouldGain_ClientAssertion(string grantType)
    {
        var (capture, log) = await SendAsync(TokenRequest(grantType, ClientId));

        var form = ParseForm(capture.Body);
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", form["client_assertion_type"]);
        var assertion = new JsonWebToken(form["client_assertion"]);
        Assert.Equal(ClientId, assertion.Issuer);
        Assert.Equal([TokenEndpoint], assertion.Audiences);
        Assert.Equal(ClientId, form["client_id"]);
        Assert.Contains(log, line => line.Contains("Attached private_key_jwt client_assertion"));
    }

    [Fact]
    public async Task TokenRequest_Aud_ShouldExclude_QueryString()
    {
        var request = TokenRequest("authorization_code", ClientId);
        request.RequestUri = new Uri(TokenEndpoint + "?foo=bar");

        var (capture, _) = await SendAsync(request);

        var assertion = new JsonWebToken(ParseForm(capture.Body)["client_assertion"]);
        Assert.Equal([TokenEndpoint], assertion.Audiences);
    }

    [Fact]
    public async Task TokenRequest_ShouldStrip_ClientSecret_AndBasicAuth()
    {
        var request = TokenRequest("authorization_code", ClientId, ("client_secret", "should-not-be-sent"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "Zm9vOmJhcg==");

        var (capture, _) = await SendAsync(request);

        Assert.DoesNotContain("client_secret", ParseForm(capture.Body).Keys);
        Assert.Null(capture.Request!.Headers.Authorization);
    }

    [Fact]
    public async Task TokenRequest_ShouldPreserve_OtherFormFields()
    {
        var request = TokenRequest(
            "authorization_code", ClientId, ("code", "abc"), ("code_verifier", "xyz"), ("redirect_uri", "https://127.0.0.1:5050/callback"));

        var (capture, _) = await SendAsync(request);

        var form = ParseForm(capture.Body);
        Assert.Equal("abc", form["code"]);
        Assert.Equal("xyz", form["code_verifier"]);
        Assert.Equal("https://127.0.0.1:5050/callback", form["redirect_uri"]);
    }

    [Fact]
    public async Task McpJsonRpcRequest_ShouldPassThrough_Unmodified()
    {
        const string body = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:5000/")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        var (capture, log) = await SendAsync(request);

        Assert.Equal(body, capture.Body);
        Assert.Empty(log);
    }

    [Fact]
    public async Task TokenRequest_ForOtherClientId_ShouldPassThrough_Unmodified()
    {
        var (capture, log) = await SendAsync(TokenRequest("authorization_code", "mcpinterop-ema-login"));

        Assert.DoesNotContain("client_assertion", ParseForm(capture.Body).Keys);
        Assert.Empty(log);
    }

    [Fact]
    public async Task TokenExchangeGrant_ShouldPassThrough_Unmodified()
    {
        var (capture, _) = await SendAsync(TokenRequest("urn:ietf:params:oauth:grant-type:token-exchange", ClientId));

        Assert.DoesNotContain("client_assertion", ParseForm(capture.Body).Keys);
    }

    private static HttpRequestMessage TokenRequest(string grantType, string clientId, params (string Key, string Value)[] extra)
    {
        var fields = new Dictionary<string, string> { ["grant_type"] = grantType, ["client_id"] = clientId };
        foreach (var (key, value) in extra)
        {
            fields[key] = value;
        }

        return new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(fields) };
    }

    private static async Task<(CapturingHandler Capture, List<string> Log)> SendAsync(HttpRequestMessage request)
    {
        var capture = new CapturingHandler();
        var log = new List<string>();
        using var invoker = new HttpMessageInvoker(
            new PrivateKeyJwtHandler(ClientId, TestSigningKeys.Shared, TimeProvider.System, log.Add, capture));

        await invoker.SendAsync(request, CancellationToken.None);
        return (capture, log);
    }

    private static Dictionary<string, string> ParseForm(string? body) =>
        QueryHelpers.ParseQuery(body).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
}
