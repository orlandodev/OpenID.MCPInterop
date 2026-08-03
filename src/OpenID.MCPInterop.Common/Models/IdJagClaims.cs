namespace OpenID.MCPInterop.Common.Models;

/// <summary>
/// Decoded claim set of an Identity Assertion Authorization Grant (ID-JAG),
/// per draft-parecki-oauth-identity-assertion-authz-grant. Used on the
/// issuing side (Issuer) to shape the token it mints, and mirrored by
/// IssuerEndpointsTests when asserting a minted token's claim shape. The
/// actual redeeming side is Keycloak (RFC 7523 JWT-bearer grant), external
/// to this repo - Server never sees a raw ID-JAG, only the ordinary OAuth
/// access token Keycloak issues after redeeming one, so nothing here
/// deserializes an incoming ID-JAG back into this type.
/// </summary>
public sealed class IdJagClaims
{
    /// <summary>iss - the enterprise IdP that issued this assertion.</summary>
    public required string Issuer { get; init; }

    /// <summary>sub - the authenticated user this assertion is about.</summary>
    public required string Subject { get; init; }

    /// <summary>aud - the target resource AS's issuer identifier. Must match exactly.</summary>
    public required string Audience { get; init; }

    /// <summary>client_id - the requesting app presenting this ID-JAG.</summary>
    public required string ClientId { get; init; }

    /// <summary>The target resource/MCP server this assertion authorizes access to.</summary>
    public string? Resource { get; init; }

    public string[]? Scope { get; init; }

    /// <summary>jti - unique ID for replay protection. Track and reject reuse.</summary>
    public required string JwtId { get; init; }

    /// <summary>exp - keep this short-lived (minutes, not hours).</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }
}
