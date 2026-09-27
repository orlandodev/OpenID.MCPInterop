using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenID.MCPInterop.Client.Auth;
using OpenID.MCPInterop.UnitTests.Support;
using Xunit;

namespace OpenID.MCPInterop.UnitTests;

public sealed class ClientAssertionFactoryTests
{
    private const string ClientId = "https://client.dev.internal:5050/client-metadata.json";
    private const string TokenEndpoint = "http://localhost:8080/realms/mcpinterop/protocol/openid-connect/token";

    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ClientAssertion_IssAndSub_ShouldMatch_CimdClientId_ByteForByte()
    {
        var token = CreateAndRead();

        Assert.Equal(ClientId, token.Issuer);
        Assert.Equal(ClientId, token.Subject);
    }

    [Fact]
    public void ClientAssertion_Aud_ShouldBe_TokenEndpointUrl()
    {
        var token = CreateAndRead();

        Assert.Equal([TokenEndpoint], token.Audiences);
    }

    [Fact]
    public void ClientAssertion_Exp_ShouldBe_ShortLived()
    {
        var token = CreateAndRead();

        Assert.Equal(Now.UtcDateTime, token.IssuedAt);
        Assert.Equal(Now.UtcDateTime.Add(ClientAssertionFactory.Lifetime), token.ValidTo);
        Assert.True(ClientAssertionFactory.Lifetime <= TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ClientAssertion_Jti_ShouldBeUnique_PerCall()
    {
        var first = CreateAndRead();
        var second = CreateAndRead();

        Assert.False(string.IsNullOrEmpty(first.Id));
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void ClientAssertion_Header_ShouldCarry_PublishedKid()
    {
        var token = CreateAndRead();

        Assert.Equal(TestSigningKeys.Shared.KeyId, token.Kid);
        Assert.Equal(SecurityAlgorithms.RsaSha256, token.Alg);
    }

    [Fact]
    public async Task ClientAssertion_ShouldValidate_AgainstPublishedJwk()
    {
        // What the AS does: verify the signature using only the public JWK
        // fetched from the CIMD document's jwks_uri.
        var assertion = ClientAssertionFactory.Create(ClientId, TokenEndpoint, TestSigningKeys.Shared.SigningCredentials, TimeProvider.System);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(assertion, new TokenValidationParameters
        {
            ValidIssuer = ClientId,
            ValidAudience = TokenEndpoint,
            IssuerSigningKey = TestSigningKeys.Shared.PublicJwk,
        });

        Assert.True(result.IsValid, result.Exception?.Message);
    }

    private static JsonWebToken CreateAndRead()
    {
        var assertion = ClientAssertionFactory.Create(ClientId, TokenEndpoint, TestSigningKeys.Shared.SigningCredentials, new FixedTimeProvider(Now));
        return new JsonWebToken(assertion);
    }
}
