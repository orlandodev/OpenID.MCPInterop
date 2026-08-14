using System.ComponentModel.DataAnnotations;

namespace OpenID.MCPInterop.Client.Options;

public sealed class ServerEndpointOptions
{
    public const string SectionName = "Server";

    [Required]
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    /// Pins McpClientOptions.ProtocolVersion to skip the SDK's discovery
    /// probe, which otherwise re-triggers OAuth mid-login against an
    /// already-auth-gated server. Only safe to set when Endpoint is this
    /// repo's own Server (known SDK/protocol version) - leave unset for an
    /// external target whose protocol version isn't known ahead of time.
    /// </summary>
    public string? ProtocolVersion { get; init; }
}
