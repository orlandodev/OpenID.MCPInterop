using OpenID.MCPInterop.Client.Auth;
using Xunit;

namespace OpenID.MCPInterop.UnitTests;

public class CimdDocumentFactoryTests
{
    [Fact]
    public void Create_ClientId_ShouldMatch_HostedDocumentUrl()
    {
        // Same footgun as CimdMetadataDocumentTests: client_id must be
        // byte-for-byte identical to the URL the document is hosted at.
        const string cimdDocumentUrl = "http://host.docker.internal:5050/client-metadata.json";

        var document = CimdDocumentFactory.Create(cimdDocumentUrl, "http://127.0.0.1:5050/callback");

        Assert.Equal(cimdDocumentUrl, document.ClientId);
    }

    [Fact]
    public void Create_RedirectUris_ShouldContain_ConfiguredRedirectUri()
    {
        const string redirectUri = "http://127.0.0.1:5050/callback";

        var document = CimdDocumentFactory.Create("http://host.docker.internal:5050/client-metadata.json", redirectUri);

        Assert.Contains(redirectUri, document.RedirectUris);
    }

    [Fact]
    public void PrivateKeyJwt_Document_ShouldAdvertise_AuthMethod_AndJwksUri()
    {
        const string jwksUri = "https://client.dev.internal:5050/jwks.json";

        var document = CimdDocumentFactory.Create("https://client.dev.internal:5050/client-metadata.json", "https://127.0.0.1:5050/callback", jwksUri);

        Assert.Equal("private_key_jwt", document.TokenEndpointAuthMethod);
        Assert.Equal(jwksUri, document.JwksUri);
    }

    [Fact]
    public void NoneAuthMethod_Document_ShouldOmit_JwksUri()
    {
        var document = CimdDocumentFactory.Create("https://client.dev.internal:5050/client-metadata.json", "https://127.0.0.1:5050/callback");

        Assert.Equal("none", document.TokenEndpointAuthMethod);
        Assert.Null(document.JwksUri);
    }
}
