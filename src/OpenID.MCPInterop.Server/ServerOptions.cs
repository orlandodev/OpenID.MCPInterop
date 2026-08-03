using System.ComponentModel.DataAnnotations;

namespace OpenID.MCPInterop.Server;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    [Required]
    public string ResourceUri { get; init; } = string.Empty;
}
