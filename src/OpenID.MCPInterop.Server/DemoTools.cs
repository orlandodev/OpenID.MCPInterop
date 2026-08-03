using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;

namespace OpenID.MCPInterop.Server;

[McpServerToolType]
public static class DemoTools
{
    [McpServerTool, Description("Returns a simple greeting to confirm the MCP server is reachable and answering tool calls.")]
    public static string Ping()
    {
        // Tag the current span with which tool ran, not the full request/response
        // body - see docs/observability.md for why bodies aren't captured wholesale
        // (size + the bearer tokens flowing through this exact call).
        Activity.Current?.SetTag("mcp.tool.name", "ping");
        return "pong from OpenID.MCPInterop.Server";
    }
}