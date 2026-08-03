using System.ComponentModel.DataAnnotations;

namespace OpenID.MCPInterop.Common.Configuration;

/// <summary>
/// The AS whose access tokens Server validates - Server extends this with
/// its own Audience field (see Server.AuthorizationOptions). Client no
/// longer binds this directly: its EMA leg has two independent authority
/// settings (see Client.EmaOptions.IdentityProviderAuthority/ResourceAuthority)
/// since the OpenID Provider that issues its subject id_token and the OAuth
/// AS that redeems its ID-JAG aren't necessarily the same system.
/// </summary>
public class AuthorityOptions
{
    public const string SectionName = "Authorization";

    [Required]
    public string Authority { get; init; } = string.Empty;

    /// <summary>False for a local-dev authority running over plain http (e.g. Keycloak on localhost) - never set false for anything reachable off this machine.</summary>
    public bool RequireHttpsMetadata { get; init; } = true;
}
