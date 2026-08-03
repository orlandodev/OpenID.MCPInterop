using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace OpenID.MCPInterop.Tests.Support;

/// <summary>
/// Stands in for the OpenID Provider Issuer's /token endpoint validates
/// subject tokens against (Issuer:IdentityProviderAuthority in the real
/// flow) - serves discovery + JWKS over a stubbed
/// <see cref="HttpMessageHandler"/> instead of a live IdP, and mints subject
/// id_tokens signed with its own in-memory RSA key so IssuerEndpointsTests can
/// exercise the real signature/issuer/expiry validation path in Issuer's
/// Endpoints.cs without needing Keycloak running anywhere.
/// </summary>
internal sealed class FakeIdentityProviderHandler : HttpMessageHandler
{
    public const string Issuer = "https://fake-idp.test";

    private static readonly string DiscoveryPath = "/.well-known/openid-configuration";
    private static readonly string JwksPath = "/.well-known/jwks.json";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly string _keyId = Guid.NewGuid().ToString("N");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath;

        if (path == DiscoveryPath)
        {
            return Task.FromResult(JsonResponse(new
            {
                issuer = Issuer,
                jwks_uri = $"{Issuer}{JwksPath}",
            }));
        }

        if (path == JwksPath)
        {
            var publicKey = RSA.Create();
            publicKey.ImportParameters(_rsa.ExportParameters(includePrivateParameters: false));
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(publicKey) { KeyId = _keyId });
            jwk.Use = "sig";
            jwk.Alg = SecurityAlgorithms.RsaSha256;

            return Task.FromResult(JsonResponse(new { keys = new[] { jwk } }));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>Mints a subject id_token signed by this fake IdP's own key - the token Client would present to Issuer's /token as subject_token.</summary>
    public string CreateIdToken(string subject, string issuer = Issuer, TimeSpan? expiresIn = null)
    {
        var signingCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = _keyId }, SecurityAlgorithms.RsaSha256);
        var lifetime = expiresIn ?? TimeSpan.FromMinutes(5);
        var expires = DateTime.UtcNow.Add(lifetime);
        // Anchor issuedAt/notBefore five minutes before expires rather than
        // at "now" - keeps notBefore < expires even when lifetime is negative
        // (an already-expired token), which JwtPayload's constructor
        // otherwise rejects outright regardless of what ValidateLifetime is
        // meant to be testing.
        var issuedAt = expires - TimeSpan.FromMinutes(5);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, subject)]),
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = expires,
            SigningCredentials = signingCredentials,
        };

        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    private static HttpResponseMessage JsonResponse(object body) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rsa.Dispose();
        }
        base.Dispose(disposing);
    }
}
