using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using OpenID.MCPInterop.Common.Constants;
using OpenID.MCPInterop.Common.Models;

namespace OpenID.MCPInterop.Issuer;

/// <summary>
/// Mints the ID-JAG JWT from the decoded claim shape defined by
/// <see cref="IdJagClaims"/> (see that type's own doc comment for who else
/// relies on this shape). Kept as a standalone static method so the
/// claim-shaping logic is unit-testable independent of the minimal-API
/// endpoint it's called from.
/// </summary>
internal static class IdJagTokenFactory
{
    public static string CreateJwt(IdJagClaims claims, SigningCredentials signingCredentials)
    {
        var subject = new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, claims.Subject),
            new Claim(OAuthConstants.ClientIdClaim, claims.ClientId),
            new Claim(JwtRegisteredClaimNames.Jti, claims.JwtId),
        ]);
        if (claims.Resource is { } resource)
        {
            subject.AddClaim(new Claim(OAuthConstants.ResourceClaim, resource));
        }
        if (claims.Scope is { Length: > 0 } scope)
        {
            subject.AddClaim(new Claim(OAuthConstants.ScopeClaim, string.Join(' ', scope)));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = claims.Issuer,
            Audience = claims.Audience,
            Subject = subject,
            IssuedAt = claims.IssuedAt.UtcDateTime,
            NotBefore = claims.IssuedAt.UtcDateTime,
            Expires = claims.ExpiresAt.UtcDateTime,
            SigningCredentials = signingCredentials,
            // draft-parecki-oauth-identity-assertion-authz-grant requires
            // this 'typ' header so a receiving AS can distinguish an ID-JAG
            // from an ordinary JWT.
            TokenType = OAuthConstants.IdJagHeaderTyp,
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.CreateEncodedJwt(descriptor);
    }
}
