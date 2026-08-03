using System.ComponentModel.DataAnnotations;

namespace OpenID.MCPInterop.Client;

public sealed class ServerEndpointOptions
{
    public const string SectionName = "Server";

    [Required]
    public string Endpoint { get; init; } = string.Empty;
}
