namespace OpenID.MCPInterop.Client.Partner;

/// <summary>
/// Disposes a previous value if present - extracted out of
/// PartnerSessionState.SetConnectedAsync purely for testability, since
/// McpClient itself has no usable public constructor to fake against.
/// </summary>
internal static class DisposalHelper
{
    public static ValueTask DisposeIfNotNullAsync(IAsyncDisposable? previous) =>
        previous is null ? ValueTask.CompletedTask : previous.DisposeAsync();
}
