using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenID.MCPInterop.Issuer;

namespace OpenID.MCPInterop.IntegrationTests.Support;

/// <summary>
/// Boots Issuer's real <see cref="Endpoints.MapIssuerEndpoints"/> against an
/// in-process <see cref="TestServer"/> - same minimal-API wiring Issuer's own
/// Program.cs uses, minus Kestrel, so tests exercise the actual request
/// pipeline (form parsing, status codes, JSON shape) rather than calling
/// endpoint delegates directly.
/// </summary>
internal sealed class IssuerTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    public HttpClient Client { get; }

    private IssuerTestHost(WebApplication app)
    {
        _app = app;
        Client = app.GetTestClient();
    }

    public static async Task<IssuerTestHost> StartAsync(IssuerOptions options, HttpMessageHandler identityProviderHandler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // The fake IdP handler stands in for the real HTTP transport Issuer's
        // subject-token discovery/JWKS fetch uses - same IHttpClientFactory
        // registration Issuer's own Program.cs makes, just with the default
        // client's primary handler swapped out.
        builder.Services
            .AddHttpClient(string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => identityProviderHandler);

        var app = builder.Build();
        app.MapIssuerEndpoints(options);

        await app.StartAsync();
        return new IssuerTestHost(app);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
