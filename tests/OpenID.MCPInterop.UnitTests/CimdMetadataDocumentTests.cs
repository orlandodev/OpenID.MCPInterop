using System.Text.Json;
using OpenID.MCPInterop.Common.Models;
using Xunit;

namespace OpenID.MCPInterop.UnitTests;

public class CimdMetadataDocumentTests
{
    [Fact]
    public void ClientId_ShouldMatch_HostedDocumentUrl()
    {
        // The single most common CIMD footgun: the client_id you present at
        // the AS must be byte-for-byte identical to the URL the metadata
        // document is actually hosted at. This test is a placeholder reminder
        // to assert that in your real setup once the Client project hosts one.
        const string hostedUrl = "https://localhost:5050/client-metadata.json";

        var document = new CimdMetadataDocument
        {
            ClientId = hostedUrl,
            RedirectUris = ["http://127.0.0.1:5100/callback"],
        };

        Assert.Equal(hostedUrl, document.ClientId);
    }

    [Fact]
    public void TokenEndpointAuthMethod_DefaultsTo_None()
    {
        var document = new CimdMetadataDocument
        {
            ClientId = "https://localhost:5050/client-metadata.json",
            RedirectUris = ["http://127.0.0.1:5100/callback"],
        };

        // Public clients using CIMD authenticate via "none" - watch for the
        // Keycloak discovery-doc bug mentioned in docs/architecture.md where
        // "none" doesn't show up in token_endpoint_auth_methods_supported yet.
        Assert.Equal("none", document.TokenEndpointAuthMethod);
    }

    [Fact]
    public void JwksUri_ShouldSerialize_As_jwks_uri()
    {
        var document = new CimdMetadataDocument
        {
            ClientId = "https://localhost:5050/client-metadata.json",
            RedirectUris = ["http://127.0.0.1:5100/callback"],
            TokenEndpointAuthMethod = "private_key_jwt",
            JwksUri = "https://localhost:5050/jwks.json",
        };

        var json = JsonSerializer.Serialize(document);

        Assert.Contains("\"jwks_uri\":\"https://localhost:5050/jwks.json\"", json);
    }

    [Fact]
    public void JwksUri_ShouldBeOmitted_ForPublicClient()
    {
        var document = new CimdMetadataDocument
        {
            ClientId = "https://localhost:5050/client-metadata.json",
            RedirectUris = ["http://127.0.0.1:5100/callback"],
        };

        var json = JsonSerializer.Serialize(document);

        Assert.DoesNotContain("jwks_uri", json);
    }
}
