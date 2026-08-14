using OpenID.MCPInterop.Client;
using OpenID.MCPInterop.Client.Auth;
using OpenID.MCPInterop.Client.Options;
using OpenID.MCPInterop.Client.State;
using OpenID.MCPInterop.Common.Configuration;
using OpenID.MCPInterop.Common.Observability;

var builder = WebApplication.CreateBuilder(args);

var clientOptions = OptionsBinder.BindAndValidate<ClientOptions>(builder.Configuration, ClientOptions.SectionName);
var serverEndpointOptions = OptionsBinder.BindAndValidate<ServerEndpointOptions>(builder.Configuration, ServerEndpointOptions.SectionName);
var emaOptions = clientOptions.UseEma
    ? OptionsBinder.BindAndValidate<EmaOptions>(builder.Configuration, EmaOptions.SectionName)
    : null;

builder.WebHost.UseUrls(clientOptions.ListenUrl);

builder.Services.AddHttpClient();
builder.Services.AddSingleton<ClientSessionState>();

// CSRF protection for the state-changing routes - see Endpoints.cs's IsValidAntiforgeryTokenAsync.
builder.Services.AddAntiforgery();

// Traces/metrics/logs exported via OTLP to a standalone Aspire Dashboard
// container - see docs/observability.md. includeMcpActivitySource: true -
// this project drives an MCP session for every scenario.
builder.Services.AddInteropObservability(builder.Environment.ApplicationName, includeMcpActivitySource: true);

var app = builder.Build();

// Serves the vendored design-system CSS (wwwroot/css) and htmx (wwwroot/lib)
// the UI shell links to - see Endpoints.cs's RenderPageOrFragment.
app.UseStaticFiles();

var cimdDocument = clientOptions.UseCimd
    ? CimdDocumentFactory.Create(clientOptions.CimdDocumentUrl!, clientOptions.RedirectUri)
    : null;

app.MapClientEndpoints(clientOptions, serverEndpointOptions, emaOptions, cimdDocument);

Console.WriteLine("OpenID.MCPInterop.Client - MCP interop test harness");
Console.WriteLine($"Scenario: {builder.Environment.EnvironmentName}");
Console.WriteLine($"Listening on: {clientOptions.ListenUrl}");

app.Run();
