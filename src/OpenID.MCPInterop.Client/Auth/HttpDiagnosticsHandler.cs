using System.Net;

namespace OpenID.MCPInterop.Client.Auth;

/// <summary>
/// Logs every HTTP request/response this connection makes, calling out the
/// 401 WWW-Authenticate challenge and the RFC 9728 protected-resource-metadata
/// fetch specifically - so this app's own Log can independently corroborate
/// what the target server observes on its side, instead of relying on
/// inference. Passed to HttpClientTransport's HttpClient-accepting
/// constructor, so it also sees the SDK's own internal PRM/discovery/token
/// calls, not just the tool-call requests this project makes directly.
/// </summary>
internal sealed class HttpDiagnosticsHandler(Action<string> log) : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        log($"{request.Method} {request.RequestUri} -> {(int)response.StatusCode} {response.StatusCode}");

        if (response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.WwwAuthenticate.Count > 0)
        {
            log($"  WWW-Authenticate: {string.Join(" ", response.Headers.WwwAuthenticate)}");
        }
        else if (request.RequestUri?.AbsolutePath.Contains("oauth-protected-resource", StringComparison.OrdinalIgnoreCase) == true
            && response.IsSuccessStatusCode)
        {
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            log($"  Protected resource metadata: {body}");
        }

        return response;
    }
}
