using System.ComponentModel.DataAnnotations;

namespace OpenID.MCPInterop.Client;

public sealed class EmaOptions
{
    public const string SectionName = "Ema";

    /// <summary>
    /// The OpenID Provider Client logs into for arrow 1's subject `id_token`
    /// - discovery/JWKS fetched from here purely to drive that authorization
    /// code + PKCE flow (see <see cref="LoginFlows.RunEmaLoginAsync"/>). In
    /// this repo's default local setup this is the same Keycloak realm as
    /// <see cref="ResourceAuthority"/>, but they don't have to be the same
    /// system - an implementer with a separate OpenID Provider and OAuth AS
    /// (Resource AS) points each setting at its own system independently.
    /// </summary>
    [Required]
    public string IdentityProviderAuthority { get; init; } = string.Empty;

    /// <summary>
    /// The Third-party OAuth AS (Resource AS) Client redeems the ID-JAG
    /// against for arrow 4 - passed as `IdentityAssertionGrantProvider`'s
    /// `authorizationServerUrl`. It has to support the RFC 7523 JWT-bearer
    /// grant for an externally-issued assertion (Keycloak's
    /// `identity-assertion-jwt` preview feature does; not every AS does yet).
    /// </summary>
    [Required]
    public string ResourceAuthority { get; init; } = string.Empty;

    /// <summary>False only for a local-dev IdentityProviderAuthority running over plain http - never set false for anything reachable off this machine.</summary>
    public bool RequireHttpsMetadata { get; init; } = true;

    /// <summary>The public PKCE client used for the EMA leg's dedicated login against <see cref="IdentityProviderAuthority"/> (arrow 1 in the architecture diagram).</summary>
    [Required]
    public string LoginClientId { get; init; } = string.Empty;

    [Required]
    public string LoginRedirectUri { get; init; } = string.Empty;

    /// <summary>
    /// The confidential Keycloak client that redeems the ID-JAG via RFC 7523
    /// JWT bearer grant. Also the client_id Issuer must embed in the ID-JAG's
    /// client_id claim (see Endpoints.cs's IdpClientId) - Keycloak requires
    /// the assertion's client_id to equal whichever client presents it at
    /// redemption, confirmed against a live instance
    /// (invalid_grant: "client id in assertion" vs "client id in request").
    /// </summary>
    [Required]
    public string ResourceClientId { get; init; } = string.Empty;

    [Required]
    public string ResourceClientSecret { get; init; } = string.Empty;

    [Required]
    public string IssuerUrl { get; init; } = string.Empty;

    public string Scope { get; init; } = "mcp:tools";
}
