namespace OpenID.MCPInterop.Client;

/// <summary>
/// The most recent tool call's outcome on a leg, for the UI's "Last
/// invocation" card. This is the real <c>CallToolAsync</c> result (success/
/// error + content) - not a governance verdict, since this app's tools have
/// no ALLOW/DENY decision concept.
/// </summary>
public sealed record ToolInvocationResult(string ToolName, bool IsError, string ResultBody, TimeSpan Elapsed, DateTimeOffset At);
