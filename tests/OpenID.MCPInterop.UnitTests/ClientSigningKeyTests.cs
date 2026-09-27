using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using OpenID.MCPInterop.Client.Auth;
using Xunit;

namespace OpenID.MCPInterop.UnitTests;

public sealed class ClientSigningKeyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mcpinterop-test-{Guid.NewGuid():N}");

    [Fact]
    public void SigningKey_ShouldReload_SameKid_AcrossRestarts()
    {
        // A regenerated key per run would break the AS's cached JWKS from the
        // CIMD jwks_uri - the persisted PEM must round-trip to the same key.
        var pemPath = Path.Combine(_directory, "keys", "cimd-signing.pem");

        var firstRun = ClientSigningKey.LoadOrCreate(pemPath);
        var secondRun = ClientSigningKey.LoadOrCreate(pemPath);

        Assert.True(File.Exists(pemPath));
        Assert.Equal(firstRun.KeyId, secondRun.KeyId);
        Assert.Equal(firstRun.PublicJwk.N, secondRun.PublicJwk.N);
    }

    [Fact]
    public void Kid_ShouldBe_Rfc7638Thumbprint()
    {
        var key = ClientSigningKey.LoadOrCreate(Path.Combine(_directory, "cimd-signing.pem"));

        Assert.Equal(Base64UrlEncoder.Encode(key.PublicJwk.ComputeJwkThumbprint()), key.KeyId);
        Assert.Equal(key.KeyId, key.PublicJwk.Kid);
    }

    [Fact]
    public void PublishedJwk_ShouldNeverContain_PrivateKeyParameters()
    {
        // CIMD section 4.1: only public keys may appear via jwks/jwks_uri.
        var key = ClientSigningKey.LoadOrCreate(Path.Combine(_directory, "cimd-signing.pem"));

        var json = JsonSerializer.Serialize(key.ToJwks());

        Assert.False(key.PublicJwk.HasPrivateKey);
        foreach (var privateParameter in new[] { "\"d\"", "\"p\"", "\"q\"", "\"dp\"", "\"dq\"", "\"qi\"" })
        {
            Assert.DoesNotContain(privateParameter, json);
        }
    }

    [Fact]
    public void ServedJwks_ShouldContain_OnlyRsaPublicMembers()
    {
        // No empty key_ops/oth/x5c arrays for a strict JWKS parser to reject.
        var key = ClientSigningKey.LoadOrCreate(Path.Combine(_directory, "cimd-signing.pem"));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(key.ToJwks()));

        var members = document.RootElement.GetProperty("keys")[0].EnumerateObject().Select(member => member.Name).Order();
        Assert.Equal(["alg", "e", "kid", "kty", "n", "use"], members);
    }

    [Fact]
    public void PublishedJwk_ShouldDeclare_SigUse_AndRs256()
    {
        var key = ClientSigningKey.LoadOrCreate(Path.Combine(_directory, "cimd-signing.pem"));

        Assert.Equal("sig", key.PublicJwk.Use);
        Assert.Equal(SecurityAlgorithms.RsaSha256, key.PublicJwk.Alg);
        Assert.Equal("RSA", key.PublicJwk.Kty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
