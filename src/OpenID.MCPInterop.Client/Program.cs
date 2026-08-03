using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenID.MCPInterop.Client;
using OpenID.MCPInterop.Common.Configuration;
using OpenID.MCPInterop.Common.Observability;

var builder = WebApplication.CreateBuilder(args);

var clientOptions = OptionsBinder.BindAndValidate<ClientOptions>(builder.Configuration, ClientOptions.SectionName);
var serverEndpointOptions = OptionsBinder.BindAndValidate<ServerEndpointOptions>(builder.Configuration, ServerEndpointOptions.SectionName);
var emaOptions = OptionsBinder.BindAndValidate<EmaOptions>(builder.Configuration, EmaOptions.SectionName);

builder.WebHost.UseUrls(clientOptions.ListenUrl);

var cimdDocumentUrl = clientOptions.CimdDocumentUrl;
var redirectUri = clientOptions.RedirectUri;
var serverEndpoint = serverEndpointOptions.Endpoint;
var scopes = clientOptions.Scopes;

// EMA leg (diagram steps 3-5) config. IdentityProviderAuthority (arrow 1:
// login for the subject id_token) and ResourceAuthority (arrow 4: redeem
// the ID-JAG) are separate settings - this repo's own local setup points
// both at the same Keycloak realm, different client registrations, but an
// implementer with a genuinely separate OpenID Provider and OAuth AS
// (Resource AS) can point each at its own system (see docs/architecture.md).
var identityProviderAuthority = emaOptions.IdentityProviderAuthority;
var resourceAuthority = emaOptions.ResourceAuthority;
var emaRequireHttpsMetadata = emaOptions.RequireHttpsMetadata;
var emaLoginClientId = emaOptions.LoginClientId;
var emaLoginRedirectUri = emaOptions.LoginRedirectUri;
var emaResourceClientId = emaOptions.ResourceClientId;
var emaResourceClientSecret = emaOptions.ResourceClientSecret;
var issuerUrl = emaOptions.IssuerUrl;
var emaScope = emaOptions.Scope;

builder.Services.AddHttpClient();

// Traces/metrics/logs exported via OTLP to a standalone Aspire Dashboard
// container - see docs/observability.md. Covers both Client's own hosted
// endpoints (/client-metadata.json, /callback, /ema-callback) and the
// outbound calls it makes to Server/Keycloak/Issuer (HttpClientInstrumentation
// instruments the MCP SDK's internal HttpClient too, not just DI-registered
// ones). includeMcpActivitySource: true - Client drives an MCP session, so
// the SDK's own tool/method-dispatch spans are worth surfacing here.
builder.Services.AddInteropObservability(builder.Environment.ApplicationName, includeMcpActivitySource: true);

var cimdDocument = CimdDocumentFactory.Create(cimdDocumentUrl, redirectUri);
var pendingAuthorizations = new ConcurrentDictionary<string, TaskCompletionSource<AuthorizationResult>>();
var pendingEmaAuthorizations = new ConcurrentDictionary<string, TaskCompletionSource<AuthorizationResult>>();

var app = builder.Build();

app.MapClientEndpoints(cimdDocument, pendingAuthorizations, pendingEmaAuthorizations);

await app.StartAsync();
try
{
    Console.WriteLine("OpenID.MCPInterop.Client - MCP CIMD / EMA interop test harness");
    Console.WriteLine($"Hosting CIMD document at: {cimdDocumentUrl}");
    Console.WriteLine("Expect two separate browser logins: first the Agent Governance (CIMD) leg, then the EMA leg's dedicated Keycloak login.");
    Console.WriteLine();

    var transport = new HttpClientTransport(new HttpClientTransportOptions
    {
        Name = "OpenID.MCPInterop.Server (local)",
        Endpoint = new Uri(serverEndpoint), // adjust to match your Server's launch profile
        // Server only speaks Streamable HTTP (WithHttpTransport(), no SSE) -
        // pin the mode instead of paying for AutoDetect's probe. Disable the
        // standalone GET stream too: it's for server-initiated pushes, and
        // DemoTools.Ping never pushes anything, so there's nothing for it to
        // carry.
        TransportMode = HttpTransportMode.StreamableHttp,
        EnableStandaloneGetStream = false,
        ConnectionTimeout = TimeSpan.FromMinutes(5),
        OAuth = new ClientOAuthOptions
        {
            ClientMetadataDocumentUri = new Uri(cimdDocumentUrl),
            RedirectUri = new Uri(redirectUri),
            Scopes = scopes,
            AuthorizationCallbackHandler = (context, cancellationToken) =>
                LoginFlows.HandleAuthorizationCallbackAsync(context, pendingAuthorizations, cancellationToken),
        },
    });

    var mcpClientOptions = new McpClientOptions
    {
        // McpClientOptions.InitializationTimeout - not HttpClientTransportOptions.
        // ConnectionTimeout above - is what actually bounds the whole ConnectAsync
        // call, interactive login included. Its default is far too short for a
        // human to switch to the browser tab and log in; learned by hitting a real
        // "Initialization timed out" mid-login against a live Keycloak instance.
        InitializationTimeout = TimeSpan.FromMinutes(5),
        // Without this, ConnectAsync probes the newer 2026-07-28 protocol
        // revision first via a server/discover request, bounded by
        // DiscoverProbeTimeout (default 5s - sized for a network round trip,
        // not a human completing a browser login). Server requires auth on
        // every request, so that probe triggers a real OAuth flow; if the
        // probe times out mid-login, the SDK abandons it uncached and falls
        // back to a second, fully independent initialize handshake with its
        // own fresh 401 - a second, distinct "Opening browser for
        // authorization" prompt with a different state/PKCE pair. Confirmed
        // against a live Keycloak instance. Pinning the version Server (same
        // SDK, same repo) actually speaks skips the probe entirely.
        ProtocolVersion = "2025-11-25",
    };

    await using var mcpClient = await McpClient.CreateAsync(transport, mcpClientOptions);

    Console.WriteLine($"Connected to: {mcpClient.ServerInfo.Name}");

    var tools = await mcpClient.ListToolsAsync();
    foreach (var tool in tools)
    {
        Console.WriteLine($"Discovered tool: {tool.Name} - {tool.Description}");
    }

    // Exercise an authenticated call, not just discovery, to prove the
    // CIMD + authorization-code+PKCE flow actually produced a token Server
    // accepts. Tag the current span with which tool this call is for - not
    // the full request/response body, see docs/observability.md for why.
    Activity.Current?.SetTag("mcp.tool.name", "ping");
    var cimdPingResult = await mcpClient.CallToolAsync("ping", new Dictionary<string, object?>());
    var cimdPingText = string.Join(", ", cimdPingResult.Content.OfType<TextContentBlock>().Select(c => c.Text));
    Console.WriteLine($"Ping result (Agent Governance / CIMD leg): {cimdPingText}");

    // EMA / cross-org leg (diagram steps 3-5): request an ID-JAG from Issuer
    // via RFC 8693 token exchange, redeem it at Keycloak via the RFC 7523 JWT
    // bearer grant (identity-assertion-jwt feature), then call the target MCP
    // server - here, the same Server instance, playing the "third-party MCP
    // server" role (see docs/architecture.md).
    Console.WriteLine();
    Console.WriteLine("Starting EMA leg - requesting an ID-JAG from Issuer, redeeming it at Keycloak...");

    var emaHttpClient = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
    var idJagProvider = new IdentityAssertionGrantProvider(
        new IdentityAssertionGrantProviderOptions
        {
            // Step 3: exchange the subject id_token for an ID-JAG at Issuer.
            // IdpClientId must be the *same* client_id that redeems the
            // ID-JAG in step 4 below (emaResourceClientId) - Keycloak checks
            // the assertion's client_id claim against whichever client
            // presents it, confirmed against a live instance (invalid_grant:
            // "client id in assertion" vs "client id in request"). Issuer's
            // own authorization policy is deliberately trivial (any validated
            // subject is allowed - see Endpoints.cs), so no client secret is
            // required for this step.
            IdpUrl = issuerUrl,
            IdpTokenEndpoint = $"{issuerUrl}/token",
            IdpClientId = emaResourceClientId,
            // Step 4: redeem the ID-JAG at Keycloak via RFC 7523 JWT bearer
            // grant, authenticating as the pre-registered confidential client
            // Keycloak's identity-assertion-jwt feature requires (see
            // deploy/keycloak/import/mcpinterop-realm.json).
            ClientId = emaResourceClientId,
            ClientSecret = emaResourceClientSecret,
            TokenEndpointAuthMethod = "client_secret_post",
            Scope = emaScope,
            IdTokenCallback = (idJagContext, cancellationToken) => LoginFlows.RunEmaLoginAsync(
                emaHttpClient, identityProviderAuthority, emaRequireHttpsMetadata, emaLoginClientId, emaLoginRedirectUri, pendingEmaAuthorizations, cancellationToken),
        },
        emaHttpClient,
        app.Services.GetRequiredService<ILoggerFactory>());

    var tokenContainer = await idJagProvider.GetAccessTokenAsync(
        resourceUrl: new Uri(serverEndpoint),
        authorizationServerUrl: new Uri(resourceAuthority),
        cancellationToken: default);

    // Plain bearer call this time - no CIMD/OAuth-discovery needed, Server
    // just validates the redeemed access token like any other bearer request.
    var emaTransport = new HttpClientTransport(new HttpClientTransportOptions
    {
        Name = "OpenID.MCPInterop.Server (EMA / third-party leg)",
        Endpoint = new Uri(serverEndpoint),
        TransportMode = HttpTransportMode.StreamableHttp,
        ConnectionTimeout = TimeSpan.FromMinutes(5),
        AdditionalHeaders = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {tokenContainer.AccessToken}",
        },
    });

    await using var emaMcpClient = await McpClient.CreateAsync(emaTransport, mcpClientOptions);
    Activity.Current?.SetTag("mcp.tool.name", "ping");
    var emaPingResult = await emaMcpClient.CallToolAsync("ping", new Dictionary<string, object?>());
    var emaPingText = string.Join(", ", emaPingResult.Content.OfType<TextContentBlock>().Select(c => c.Text));
    Console.WriteLine($"Ping result (EMA / cross-org leg): {emaPingText}");

    // Both legs are done, but keep the host up so the CIMD document is still
    // browsable afterward (e.g. to eyeball it in a browser, or point another
    // interop participant's tooling at it) instead of tearing it down the
    // instant the demo calls finish.
    Console.WriteLine();
    Console.WriteLine($"CIMD document (client_id) URL: {cimdDocumentUrl}");
    // client.dev.internal only resolves inside the Keycloak container, via
    // the extra_hosts entry deploy/keycloak/setup.sh writes - your own
    // machine's OS never gets that mapping, so a regular browser can't
    // resolve it (confirmed: DNS failure trying to browse it directly).
    // Kestrel doesn't route on Host header here, so the exact same content
    // is reachable from this machine at the loopback equivalent below.
    var browsableCimdDocumentUrl = new UriBuilder(cimdDocumentUrl) { Host = "localhost" }.Uri;
    Console.WriteLine($"Browse it from this machine at: {browsableCimdDocumentUrl}");
    // uncomment if you want to see the full CIMD document JSON in the console output.
    // Console.WriteLine(JsonSerializer.Serialize(cimdDocument, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine();
    Console.WriteLine("Client is still running (Ctrl+C to exit).");
    await app.WaitForShutdownAsync();
}
finally
{
    await app.StopAsync();
}
