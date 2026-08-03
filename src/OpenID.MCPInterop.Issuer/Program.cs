using OpenID.MCPInterop.Common.Configuration;
using OpenID.MCPInterop.Common.Observability;
using OpenID.MCPInterop.Issuer;

var builder = WebApplication.CreateBuilder(args);

var issuerOptions = OptionsBinder.BindAndValidate<IssuerOptions>(builder.Configuration, IssuerOptions.SectionName);

builder.WebHost.UseUrls(issuerOptions.ListenUrl);

// Needed to fetch Keycloak's discovery document/JWKS when validating
// incoming subject tokens (see Endpoints.cs).
builder.Services.AddHttpClient();

// Traces/metrics/logs exported via OTLP to a standalone Aspire Dashboard
// container - see docs/observability.md. HttpClientInstrumentation matters
// here: Issuer calls out to Keycloak's JWKS endpoint to validate incoming
// subject tokens. Issuer doesn't speak MCP itself, so no MCP activity source.
builder.Services.AddInteropObservability(builder.Environment.ApplicationName);

var app = builder.Build();

app.MapIssuerEndpoints(issuerOptions);

app.Run();
