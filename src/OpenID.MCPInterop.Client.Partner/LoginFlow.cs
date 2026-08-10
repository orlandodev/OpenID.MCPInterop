using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol.Authentication;

namespace OpenID.MCPInterop.Client.Partner;

/// <summary>
/// Handles the MCP SDK's <see cref="ClientOAuthOptions.AuthorizationCallbackHandler"/>
/// for the direct-trust leg. Unlike Client's console-only LoginFlows (which
/// opens a new browser tab via Process.Start), this redirects the browser
/// that clicked "Connect" via <paramref name="authorizationUriReady"/>, then
/// waits on the same state-keyed-TaskCompletionSource pattern as /callback.
/// </summary>
/// <remarks>
/// The state-parsing/TaskCompletionSource portion mirrors
/// <see cref="OpenID.MCPInterop.Client.LoginFlows"/> almost verbatim - see
/// that class's remarks for why this isn't factored into Common. Keep both
/// in sync by hand if this logic changes.
/// </remarks>
internal static class LoginFlow
{
    public static async Task<AuthorizationResult?> HandleAuthorizationCallbackAsync(
        AuthorizationCallbackContext context,
        PartnerSessionState session,
        TaskCompletionSource<Uri> authorizationUriReady,
        CancellationToken cancellationToken)
    {
        var authorizationQuery = QueryHelpers.ParseQuery(context.AuthorizationUri.Query);
        if (!authorizationQuery.TryGetValue("state", out var stateValues) || stateValues.Count == 0)
        {
            throw new InvalidOperationException("Authorization URI did not include a state parameter.");
        }

        var state = stateValues[0]!;
        var tcs = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PendingCallbacks[state] = tcs;

        session.AppendLog($"Authorization server: {context.AuthorizationUri.GetLeftPart(UriPartial.Authority)}");
        authorizationUriReady.TrySetResult(context.AuthorizationUri);

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return await tcs.Task;
    }
}
