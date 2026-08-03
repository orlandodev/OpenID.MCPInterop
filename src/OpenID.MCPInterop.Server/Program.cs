using Microsoft.AspNetCore.Authentication.JwtBearer;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using OpenID.MCPInterop.Common.Configuration;
using OpenID.MCPInterop.Common.Observability;
using OpenID.MCPInterop.Server;

var builder = WebApplication.CreateBuilder(args);

var authorizationOptions = OptionsBinder.BindAndValidate<AuthorizationOptions>(builder.Configuration, AuthorizationOptions.SectionName);
var serverOptions = OptionsBinder.BindAndValidate<ServerOptions>(builder.Configuration, ServerOptions.SectionName);

var authority = authorizationOptions.Authority;
var audience = authorizationOptions.Audience;
var requireHttpsMetadata = authorizationOptions.RequireHttpsMetadata;
var resourceUri = serverOptions.ResourceUri;

// Traces/metrics/logs exported via OTLP to a standalone Aspire Dashboard
// container - see docs/observability.md. Reads OTEL_EXPORTER_OTLP_ENDPOINT
// (defaults to http://localhost:4317) - no endpoint config needed here.
// includeMcpActivitySource: true - Server acts as an MCP endpoint, so the
// SDK's own tool/method-dispatch spans are worth surfacing here.
builder.Services.AddInteropObservability(builder.Environment.ApplicationName, includeMcpActivitySource: true);

// Agent Governance leg (diagram steps 1-2): reject unauthenticated calls and
// validate the access token issued after the CIMD-based flow. The Mcp scheme
// handles 401 challenges (RFC 9728 protected-resource metadata, served
// automatically at the MCP well-known path); JwtBearer does the actual token
// validation against Keycloak. See docs/keycloak-setup.md for standing up an
// Authority that satisfies this - in particular, Keycloak doesn't support
// RFC 8707 resource indicators yet, so the `aud` claim only shows up if you
// add a client-scope audience mapper (Included Custom Audience = Audience
// below) and Client requests that scope.
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = requireHttpsMetadata;
    })
    .AddMcp(options =>
    {
        options.ResourceMetadata = new ProtectedResourceMetadata
        {
            Resource = resourceUri,
            AuthorizationServers = [authority],
            ScopesSupported = ["mcp:tools"],
            BearerMethodsSupported = ["header"],
        };
    });

builder.Services.AddAuthorization();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

// EMA leg (diagram steps 3-5, this instance playing the "Third-party MCP
// Server" role): no extra validation needed here beyond the JwtBearer/Mcp
// wiring above. Client redeems the ID-JAG at the *same* Keycloak realm
// (RFC 7523 JWT bearer grant) that issues the CIMD leg's tokens, so the
// resulting access token is validated identically - same Authority, same
// mcp:tools audience mapper - regardless of which grant type produced it.

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapMcp().RequireAuthorization();

app.Run();
