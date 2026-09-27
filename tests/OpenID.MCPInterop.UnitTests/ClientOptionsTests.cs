using System.ComponentModel.DataAnnotations;
using OpenID.MCPInterop.Client.Options;
using Xunit;

namespace OpenID.MCPInterop.UnitTests;

public sealed class ClientOptionsTests
{
    private const string CimdDocumentUrl = "https://client.dev.internal:5050/client-metadata.json";

    [Fact]
    public void CimdAuthMethod_ShouldDefaultTo_PrivateKeyJwt()
    {
        var options = new ClientOptions { UseCimd = true, CimdDocumentUrl = CimdDocumentUrl, RedirectUri = "https://127.0.0.1:5050/callback" };

        Assert.True(options.UsePrivateKeyJwt);
    }

    [Fact]
    public void UnknownCimdAuthMethod_ShouldFail_Validation()
    {
        var options = new ClientOptions
        {
            UseCimd = true,
            CimdDocumentUrl = CimdDocumentUrl,
            CimdAuthMethod = "client_secret_basic",
            RedirectUri = "https://127.0.0.1:5050/callback",
        };

        var results = options.Validate(new ValidationContext(options)).ToList();

        Assert.Contains(results, result => result.MemberNames.Contains(nameof(ClientOptions.CimdAuthMethod)));
    }

    [Theory]
    [InlineData("https://client.dev.internal:5050/client-metadata.json", "https://client.dev.internal:5050/jwks.json")]
    [InlineData("https://some-tunnel.ngrok-free.dev/client-metadata.json", "https://some-tunnel.ngrok-free.dev/jwks.json")]
    public void JwksUri_ShouldBe_SameOrigin_AsCimdDocumentUrl(string cimdDocumentUrl, string expectedJwksUri)
    {
        var options = new ClientOptions { UseCimd = true, CimdDocumentUrl = cimdDocumentUrl };

        Assert.Equal(expectedJwksUri, options.JwksUri);
    }

    [Fact]
    public void NoneAuthMethod_ShouldHave_NoJwksUri()
    {
        var options = new ClientOptions { UseCimd = true, CimdDocumentUrl = CimdDocumentUrl, CimdAuthMethod = "none" };

        Assert.False(options.UsePrivateKeyJwt);
        Assert.Null(options.JwksUri);
    }

    [Fact]
    public void DirectTrust_ShouldNeverUse_PrivateKeyJwt()
    {
        var options = new ClientOptions { UseCimd = false, ClientId = "abc", Authority = "https://as.example" };

        Assert.False(options.UsePrivateKeyJwt);
        Assert.Null(options.JwksUri);
    }
}
