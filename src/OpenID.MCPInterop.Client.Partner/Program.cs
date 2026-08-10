using OpenID.MCPInterop.Client.Partner;
using OpenID.MCPInterop.Common.Configuration;
using OpenID.MCPInterop.Common.Observability;

var builder = WebApplication.CreateBuilder(args);

var partnerOptions = OptionsBinder.BindAndValidate<PartnerOptions>(builder.Configuration, PartnerOptions.SectionName);

builder.WebHost.UseUrls(partnerOptions.ListenUrl);

builder.Services.AddSingleton(partnerOptions);
builder.Services.AddSingleton<PartnerSessionState>();

// CSRF protection for the state-changing routes - see Endpoints.cs's IsValidAntiforgeryTokenAsync.
builder.Services.AddAntiforgery();

// Traces/metrics/logs exported via OTLP to a standalone Aspire Dashboard
// container - see docs/observability.md. includeMcpActivitySource: true -
// this project drives an MCP session, same as Client.
builder.Services.AddInteropObservability(builder.Environment.ApplicationName, includeMcpActivitySource: true);

var app = builder.Build();

app.MapPartnerEndpoints();

Console.WriteLine("OpenID.MCPInterop.Client.Partner - direct-trust leg demo UI");
Console.WriteLine($"Listening on: {partnerOptions.ListenUrl}");
Console.WriteLine($"Target MCP server: {partnerOptions.TargetServerEndpoint}");

app.Run();
