using ModelContextProtocol.Client;

namespace OpenID.MCPInterop.Client.Helpers;

/// <summary>
/// Safe access to <see cref="McpClient.ServerInfo"/>, which throws rather than
/// returning null when a server omits its (optional) identity metadata - e.g.
/// one answering via <c>server/discover</c> without <c>serverInfo</c>. The SDK
/// offers no try-get, so the exception is the only signal available.
/// </summary>
internal static class McpClientExtensions
{
    public static string? TryGetServerName(this McpClient client)
    {
        try
        {
            return client.ServerInfo.Name;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
