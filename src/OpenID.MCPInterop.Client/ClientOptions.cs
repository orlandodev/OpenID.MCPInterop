using System.ComponentModel.DataAnnotations;

namespace OpenID.MCPInterop.Client;

public sealed class ClientOptions
{
    public const string SectionName = "Client";

    [Required]
    public string CimdDocumentUrl { get; init; } = string.Empty;

    [Required]
    public string RedirectUri { get; init; } = string.Empty;

    public string ListenUrl { get; init; } = "http://0.0.0.0:5050";

    public string[] Scopes { get; init; } = ["openid", "mcp:tools"];
}
