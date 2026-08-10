using System.ComponentModel.DataAnnotations;

namespace OpenID.MCPInterop.Client.Partner;

/// <summary>
/// Config for the direct-trust leg: a normal, pre-registered OAuth 2.1
/// client at <see cref="Authority"/> calling <see cref="TargetServerEndpoint"/> -
/// an external resource server that allowlists trusted issuers directly, no
/// CIMD document and no ID-JAG/token-exchange involved (see
/// docs/architecture.md's "Direct-trust leg" section for how this differs
/// from Client's CIMD and EMA legs).
/// </summary>
public sealed class PartnerOptions
{
    public const string SectionName = "Partner";

    /// <summary>
    /// The hosted AS's issuer URL - sent to the partner to allowlist, and used
    /// to disambiguate <see cref="TargetServerEndpoint"/>'s advertised AS list
    /// if it has more than one (see PartnerAuthServerSelector).
    /// </summary>
    [Required]
    public string Authority { get; init; } = string.Empty;

    [Required]
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Null for a public client (e.g. PKCE-only); set for a confidential client registration.</summary>
    public string? ClientSecret { get; init; }

    [Required]
    public string RedirectUri { get; init; } = string.Empty;

    /// <summary>
    /// What Kestrel binds to. Stays loopback-friendly by default - the OAuth
    /// redirect only needs to reach whichever browser is doing the human
    /// login, not the public internet (see docs/architecture.md).
    /// </summary>
    public string ListenUrl { get; init; } = "https://0.0.0.0:5070";

    [Required]
    public string TargetServerEndpoint { get; init; } = string.Empty;

    public string[] Scopes { get; init; } = [];
}
