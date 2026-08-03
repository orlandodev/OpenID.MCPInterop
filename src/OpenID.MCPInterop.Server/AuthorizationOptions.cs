using System.ComponentModel.DataAnnotations;

namespace OpenID.MCPInterop.Server;

/// <summary>
/// Extends the shared Authority/RequireHttpsMetadata shape (see Common.Configuration.AuthorityOptions) with the audience Server validates access tokens against.
/// </summary>
public sealed class AuthorizationOptions : Common.Configuration.AuthorityOptions
{
    [Required]
    public string Audience { get; init; } = string.Empty;
}
