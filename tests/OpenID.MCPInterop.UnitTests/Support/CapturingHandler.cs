using System.Net;

namespace OpenID.MCPInterop.UnitTests.Support;

/// <summary>Terminal handler that records the last request (and its body, read before disposal) instead of sending it.</summary>
internal sealed class CapturingHandler : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }

    public string? Body { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
