namespace OpenID.MCPInterop.Client.Helpers;

/// <summary>
/// Disposes a previous value if present - extracted out of
/// ClientSessionState.SetConnectedAsync purely for testability, since
/// McpClient itself has no usable public constructor to fake against.
/// </summary>
internal static class DisposalHelper
{
    public static ValueTask DisposeIfNotNullAsync(IAsyncDisposable? previous) =>
        previous is null ? ValueTask.CompletedTask : previous.DisposeAsync();
}
