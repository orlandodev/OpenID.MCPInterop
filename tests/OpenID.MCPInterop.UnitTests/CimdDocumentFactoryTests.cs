using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Configuration;
using OpenID.MCPInterop.Client.Auth;
using OpenID.MCPInterop.Client.Options;
using OpenID.MCPInterop.Common.Configuration;
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
    public void PublishedDocument_ShouldMatch_GitHubPagesScenarioConfig()
    {
        // cimd/client-metadata.json is a static file on GitHub Pages, so
        // nothing regenerates it when the factory, CimdMetadataDocument or
        // the GitHubPages scenario's CimdDocumentUrl/RedirectUri change - an
        // AS would then reject the redirect_uri or client_id the Client
        // actually presents. Republish the file whenever this fails.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "Published", "appsettings.GitHubPages.json"))
            .Build();
        var options = OptionsBinder.BindAndValidate<ClientOptions>(configuration, ClientOptions.SectionName);

        var generated = JsonSerializer.SerializeToNode(
            CimdDocumentFactory.Create(options.CimdDocumentUrl!, options.RedirectUri, options.JwksUri));
        var published = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Published", "client-metadata.json")));

        Assert.True(JsonNode.DeepEquals(generated, published), $"cimd/client-metadata.json is stale. Expected:{Environment.NewLine}{generated}");
    }

    [Fact]
    public void NoneAuthMethod_Document_ShouldOmit_JwksUri()
    {
        var document = CimdDocumentFactory.Create("https://client.dev.internal:5050/client-metadata.json", "https://127.0.0.1:5050/callback");

        Assert.Equal("none", document.TokenEndpointAuthMethod);
        Assert.Null(document.JwksUri);
    }
}
