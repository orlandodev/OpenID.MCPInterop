using System.Security.Claims;
using Duende.IdentityModel;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace OpenID.MCPInterop.Client.Auth;

/// <summary>
/// Mints the RFC 7523 section 2.2 client_assertion JWT, with claims per
/// section 3: iss and sub are both the CIMD client_id URL (byte-for-byte,
/// same footgun as the hosted document), aud is the token endpoint it's being
/// presented to, and jti/exp keep it single-use and short-lived. Duende's
/// protocol libraries only carry an assertion, they don't mint one, so this
/// signs with Microsoft.IdentityModel directly.
/// </summary>
internal static class ClientAssertionFactory
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    public static string Create(string clientId, string audience, SigningCredentials signingCredentials, TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = audience,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtClaimTypes.Subject, clientId),
                new Claim(JwtClaimTypes.JwtId, Guid.NewGuid().ToString("N")),
            ]),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(Lifetime),
            SigningCredentials = signingCredentials,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
