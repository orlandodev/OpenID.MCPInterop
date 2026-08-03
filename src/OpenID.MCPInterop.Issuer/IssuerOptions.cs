using System.ComponentModel.DataAnnotations;

namespace OpenID.MCPInterop.Issuer;

public sealed class IssuerOptions
{
    public const string SectionName = "Issuer";

    /// <summary>
    /// Issuer's own externally-reachable identity: baked into every minted
    /// ID-JAG's iss claim and into the discovery document's issuer field, so
    /// it must exactly match whatever Keycloak's identity-provider config
    /// registers as this IdP's issuer (see deploy/keycloak/import). Not
    /// necessarily the same host Issuer itself binds to - see ListenUrl -
    /// same client.dev.internal-vs-0.0.0.0 split the CIMD leg's Client
    /// already uses, and for the same reason: Keycloak fetches this
    /// server-to-server from inside its container, where 'localhost' means
    /// the container itself, not this machine (confirmed against a live
    /// instance: reload-keys failed with Connection refused). Required - an
    /// omitted value used to silently fall back to localhost:5100, which
    /// would mismatch Keycloak's registered issuer with no startup error.
    /// </summary>
    [Required]
    public string Url { get; init; } = string.Empty;

    /// <summary>What Kestrel actually binds to - 0.0.0.0 accepts the request whichever hostname reached it.</summary>
    public string ListenUrl { get; init; } = "http://0.0.0.0:5100";

    /// <summary>
    /// The OpenID Provider whose discovery/JWKS validates incoming
    /// subject_token values (arrow 1/3's login IdP). Not necessarily the same
    /// system as <see cref="ResourceAuthority"/> - this repo's own local
    /// setup happens to point both at the same Keycloak realm, but an
    /// implementer with a genuinely separate OpenID Provider and OAuth AS
    /// (Resource AS) sets each independently.
    /// </summary>
    [Required]
    public string IdentityProviderAuthority { get; init; } = string.Empty;

    /// <summary>
    /// The Third-party OAuth AS (Resource AS) this Issuer mints ID-JAGs for -
    /// baked into every minted ID-JAG's aud claim (arrow 4's target). Must
    /// exactly match that AS's own issuer identifier, since it validates the
    /// aud claim on redemption.
    /// </summary>
    [Required]
    public string ResourceAuthority { get; init; } = string.Empty;

    /// <summary>
    /// False for a local-dev IdentityProviderAuthority running over plain http - never set false for anything reachable off this machine.
    /// </summary>
    public bool RequireHttpsMetadata { get; init; } = true;

    /// <summary>
    /// client_id values /token will mint an ID-JAG for; anything else is
    /// rejected before the (more expensive) subject_token validation runs.
    /// Issuer's subject check alone doesn't constrain which client an
    /// authenticated caller can claim to be - this closes that gap.
    /// </summary>
    [Required, MinLength(1)]
    public string[] TrustedClientIds { get; init; } = [];
}
