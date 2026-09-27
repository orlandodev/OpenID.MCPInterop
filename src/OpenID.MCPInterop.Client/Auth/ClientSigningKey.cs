using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace OpenID.MCPInterop.Client.Auth;

/// <summary>
/// The RSA key pair this Client signs its private_key_jwt client assertions
/// with (CIMD section 8.2 / RFC 7523 section 2.2). Unlike Issuer's
/// per-run key, this one is persisted to a PEM file on first run and reloaded
/// afterwards: the AS caches the JWKS it fetched from the CIMD document's
/// jwks_uri, and a regenerated key would fail signature validation until that
/// cache expires - which can't be forced on a third-party AS. The kid is the
/// RFC 7638 thumbprint, so it's deterministic from the key itself.
/// </summary>
internal sealed class ClientSigningKey
{
    private ClientSigningKey(RSA rsa)
    {
        var publicRsa = RSA.Create();
        publicRsa.ImportParameters(rsa.ExportParameters(includePrivateParameters: false));

        PublicJwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(publicRsa));
        KeyId = Base64UrlEncoder.Encode(PublicJwk.ComputeJwkThumbprint());
        PublicJwk.Kid = KeyId;
        PublicJwk.Use = JsonWebKeyUseNames.Sig;
        PublicJwk.Alg = SecurityAlgorithms.RsaSha256;

        SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256);
    }

    public string KeyId { get; }

    /// <summary>Public half only - safe to publish at the jwks_uri.</summary>
    public JsonWebKey PublicJwk { get; }

    public SigningCredentials SigningCredentials { get; }

    /// <summary>
    /// The JWK Set document served at the jwks_uri. Projected to just the
    /// RFC 7517/7518 RSA public members rather than serializing
    /// <see cref="JsonWebKey"/> directly, which would also emit its empty
    /// key_ops/oth/x5c collections for a strict third-party parser to trip on.
    /// </summary>
    public object ToJwks() => new
    {
        keys = new[]
        {
            new
            {
                kty = PublicJwk.Kty,
                use = PublicJwk.Use,
                alg = PublicJwk.Alg,
                kid = PublicJwk.Kid,
                n = PublicJwk.N,
                e = PublicJwk.E,
            },
        },
    };

    public static ClientSigningKey LoadOrCreate(string pemPath)
    {
        var rsa = RSA.Create(2048);

        if (File.Exists(pemPath))
        {
            rsa.ImportFromPem(File.ReadAllText(pemPath));
            return new ClientSigningKey(rsa);
        }

        var directory = Path.GetDirectoryName(pemPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(pemPath, rsa.ExportPkcs8PrivateKeyPem());
        return new ClientSigningKey(rsa);
    }
}
