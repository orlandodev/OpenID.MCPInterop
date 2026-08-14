using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenID.MCPInterop.Client.Options;
using OpenID.MCPInterop.Common.Models;

namespace OpenID.MCPInterop.Client;

/// <summary>
/// Maps every route this Client hosts, for every scenario: the web UI (home
/// page, connect/cancel/invoke for the primary leg and, when
/// <see cref="ClientOptions.UseEma"/>, the EMA leg too), the CIMD document
/// itself (when <see cref="ClientOptions.UseCimd"/>), and the loopback
/// callback targets both legs' authorization-code+PKCE flows redirect back
/// to. See LoginFlows.cs for why every leg redirects the browser that
/// clicked a button rather than opening a new tab.
///
/// The UI is styled against the vendored industry-styles.css/mcp-client.css
/// (wwwroot/css) and lightly interactive via htmx (wwwroot/lib) - every
/// navigational/action route below renders the same <c>.mcp-app</c> shell
/// (<see cref="ClientHtmlRenderer.RenderAppShell"/>, in its own file to keep
/// markup out of this one) and returns either the full HTML document (a real
/// top-level navigation, or JS disabled) or just that shell fragment for
/// htmx to swap into <c>#mcp-app</c>, decided by the <c>HX-Request</c>
/// header (see <see cref="IsHtmxRequest"/>). The OAuth <c>/connect</c>/
/// <c>/ema-connect</c> forms are the one exception - they always do a real
/// 302 to the external IdP, which htmx can't drive correctly, so they're
/// plain form posts regardless of how they're requested.
/// </summary>
public static class Endpoints
{
    public static WebApplication MapClientEndpoints(
        this WebApplication app,
        ClientOptions options,
        ServerEndpointOptions serverEndpointOptions,
        EmaOptions? emaOptions,
        CimdMetadataDocument? cimdDocument)
    {
        if (options.UseCimd && cimdDocument is not null)
        {
            // This URL literally *is* the client_id, per
            // draft-ietf-oauth-client-id-metadata-document - the AS fetches
            // it server-to-server the first time it sees this client_id at
            // the authorization endpoint.
            app.MapGet("/client-metadata.json", () => Results.Json(cimdDocument));
        }

        app.MapGet("/", (HttpContext context, IAntiforgery antiforgery, ClientSessionState session) =>
            RenderPageOrFragment(context, antiforgery, session, options, serverEndpointOptions, emaOptions));

        app.MapGet("/servers/{leg}", (string leg, HttpContext context, IAntiforgery antiforgery, ClientSessionState session) =>
        {
            session.SetSelectedLeg(NormalizeLeg(leg, options));
            return RenderPageOrFragment(context, antiforgery, session, options, serverEndpointOptions, emaOptions);
        });

        app.MapGet("/tools/{leg}/{name}", (string leg, string name, HttpContext context, IAntiforgery antiforgery, ClientSessionState session) =>
        {
            var normalizedLeg = NormalizeLeg(leg, options);
            session.SetSelectedLeg(normalizedLeg);
            session.SetSelectedTool(normalizedLeg, name);
            return RenderPageOrFragment(context, antiforgery, session, options, serverEndpointOptions, emaOptions);
        });

        app.MapPost("/connect", async (HttpContext context, IAntiforgery antiforgery, ClientSessionState session) =>
        {
            if (!await IsValidAntiforgeryTokenAsync(antiforgery, context, session))
            {
                return Results.BadRequest("Invalid or missing anti-forgery token.");
            }

            if (!session.TryBeginConnecting(out var cancellationToken))
            {
                return Results.Redirect("/");
            }

            session.AppendLog($"Connecting to {serverEndpointOptions.Endpoint}...");

            var authorizationUriReady = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

            _ = Task.Run(() => RunConnectionAsync(options, serverEndpointOptions, session, authorizationUriReady, cancellationToken));

            try
            {
                // Waits on the PRM/discovery round trip, not on the human login.
                var authorizationUri = await authorizationUriReady.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                return Results.Redirect(authorizationUri.ToString());
            }
            catch (Exception ex)
            {
                // RunConnectionAsync's own catch already logs/sets Status for
                // anything it throws directly - only ReportTimeout is new here.
                if (ConnectFailureReporter.Classify(ex) == ConnectFailureAction.ReportTimeout)
                {
                    session.AppendLog("Could not start login: timed out waiting for the authorization URL.");
                    session.SetStatus(ClientConnectionStatus.Failed);
                }

                return Results.Redirect("/");
            }
        });

        app.MapPost("/ema-connect", async (HttpContext context, IAntiforgery antiforgery, ClientSessionState session, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory) =>
        {
            if (!await IsValidAntiforgeryTokenAsync(antiforgery, context, session))
            {
                return Results.BadRequest("Invalid or missing anti-forgery token.");
            }

            if (!options.UseEma || emaOptions is null)
            {
                session.AppendLog("The EMA leg is not enabled for this scenario.");
                return Results.Redirect("/");
            }

            if (!session.TryBeginEmaConnecting(out var cancellationToken))
            {
                return Results.Redirect("/");
            }

            session.AppendLog("Starting EMA leg - requesting an ID-JAG from Issuer, redeeming it at Keycloak...");

            var emaAuthorizationUriReady = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

            _ = Task.Run(() => RunEmaConnectionAsync(
                serverEndpointOptions, emaOptions, session, emaAuthorizationUriReady, httpClientFactory, loggerFactory, cancellationToken));

            try
            {
                var authorizationUri = await emaAuthorizationUriReady.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                return Results.Redirect(authorizationUri.ToString());
            }
            catch (Exception ex)
            {
                if (ConnectFailureReporter.Classify(ex) == ConnectFailureAction.ReportTimeout)
                {
                    session.AppendLog("Could not start EMA login: timed out waiting for the authorization URL.");
                    session.SetEmaStatus(ClientConnectionStatus.Failed);
                }

                return Results.Redirect("/");
            }
        });

        app.MapPost("/cancel", async (HttpContext context, IAntiforgery antiforgery, ClientSessionState session) =>
        {
            if (!await IsValidAntiforgeryTokenAsync(antiforgery, context, session))
            {
                return Results.BadRequest("Invalid or missing anti-forgery token.");
            }

            session.CancelConnecting();
            return Results.Redirect("/");
        });

        app.MapPost("/ema-cancel", async (HttpContext context, IAntiforgery antiforgery, ClientSessionState session) =>
        {
            if (!await IsValidAntiforgeryTokenAsync(antiforgery, context, session))
            {
                return Results.BadRequest("Invalid or missing anti-forgery token.");
            }

            session.CancelEmaConnecting();
            return Results.Redirect("/");
        });

        app.MapGet("/callback", (HttpContext context, ClientSessionState session) =>
            HandleCallback(context, session.PendingCallbacks, session));

        app.MapGet("/ema-callback", (HttpContext context, ClientSessionState session) =>
            HandleCallback(context, session.PendingEmaCallbacks, session));

        app.MapPost("/log/clear", async (HttpContext context, IAntiforgery antiforgery, ClientSessionState session) =>
        {
            if (!await IsValidAntiforgeryTokenAsync(antiforgery, context, session))
            {
                return Results.BadRequest("Invalid or missing anti-forgery token.");
            }

            session.ClearLog();
            return RenderPageOrFragment(context, antiforgery, session, options, serverEndpointOptions, emaOptions);
        });

        app.MapPost("/tools/{leg}/{name}/invoke", async (string leg, string name, HttpContext context, IAntiforgery antiforgery, ClientSessionState session) =>
        {
            if (!await IsValidAntiforgeryTokenAsync(antiforgery, context, session))
            {
                return Results.BadRequest("Invalid or missing anti-forgery token.");
            }

            var normalizedLeg = NormalizeLeg(leg, options);
            if (leg is not ("primary" or "ema"))
            {
                session.AppendLog($"Unrecognized leg '{leg}' - cannot invoke a tool.");
                return Results.Redirect("/");
            }

            var client = normalizedLeg == "primary" ? session.Client : session.EmaClient;
            if (client is null)
            {
                session.AppendLog($"Cannot invoke a tool on the {normalizedLeg} leg before connecting.");
                return Results.Redirect("/");
            }

            session.SetSelectedLeg(normalizedLeg);
            session.SetSelectedTool(normalizedLeg, name);

            var tools = normalizedLeg == "primary" ? session.Tools : session.EmaTools;
            var tool = tools.FirstOrDefault(t => t.Name == name);
            var form = await context.Request.ReadFormAsync();
            var arguments = BuildArguments(form, ClientHtmlRenderer.ParseSchemaFields(tool.Schema));

            // Bounds the wait so a tool call that never responds (e.g. a stuck
            // external control-plane check) fails predictably instead of hanging.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var result = await client.CallToolAsync(name, arguments, cancellationToken: timeoutCts.Token);
                stopwatch.Stop();

                // Tool failures come back as a normal result with IsError true
                // (per MCP spec), not a thrown exception - and content isn't
                // always plain text, so both need surfacing here.
                var contentSummary = result.Content.Count == 0
                    ? "(no content)"
                    : string.Join(" | ", result.Content.Select(DescribeContent));
                var structuredSummary = result.StructuredContent is { } structured
                    ? $" structuredContent: {structured.GetRawText()}"
                    : string.Empty;
                var isError = result.IsError == true;
                var outcome = isError ? "ERROR" : "result";
                session.AppendLog($"Tool '{name}' ({normalizedLeg}) {outcome}: {contentSummary}{structuredSummary}");
                session.SetLastResult(normalizedLeg, new ToolInvocationResult(name, isError, contentSummary + structuredSummary, stopwatch.Elapsed, DateTimeOffset.Now));
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                stopwatch.Stop();
                session.AppendLog($"Tool '{name}' ({normalizedLeg}) timed out after 30s with no response from the server.");
                session.SetLastResult(normalizedLeg, new ToolInvocationResult(name, true, "(timed out after 30s)", stopwatch.Elapsed, DateTimeOffset.Now));
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                session.AppendLog($"Tool '{name}' ({normalizedLeg}) failed: {ex.Message}");
                session.SetLastResult(normalizedLeg, new ToolInvocationResult(name, true, ex.Message, stopwatch.Elapsed, DateTimeOffset.Now));
            }

            return RenderPageOrFragment(context, antiforgery, session, options, serverEndpointOptions, emaOptions);
        });

        return app;
    }

    /// <summary>Falls back to "primary" for an unrecognized or (when EMA isn't enabled for this scenario) unavailable leg.</summary>
    internal static string NormalizeLeg(string leg, ClientOptions options) =>
        leg == "ema" && options.UseEma ? "ema" : "primary";

    private static bool IsHtmxRequest(HttpContext context) =>
        context.Request.Headers.TryGetValue("HX-Request", out var value) && value == "true";

    /// <summary>
    /// Every navigational/action route renders through here: a real top-level
    /// request (no HX-Request header - a fresh page load, a bookmarked link,
    /// or JS disabled) gets the full HTML document; an htmx request gets just
    /// the <c>.mcp-app</c> shell fragment to swap in.
    /// </summary>
    private static IResult RenderPageOrFragment(
        HttpContext context,
        IAntiforgery antiforgery,
        ClientSessionState session,
        ClientOptions options,
        ServerEndpointOptions serverEndpointOptions,
        EmaOptions? emaOptions)
    {
        var antiforgeryTokens = antiforgery.GetAndStoreTokens(context);
        var shellHtml = ClientHtmlRenderer.RenderAppShell(session, options, serverEndpointOptions, emaOptions, antiforgeryTokens);

        if (IsHtmxRequest(context))
        {
            return Results.Content(shellHtml, "text/html");
        }

        return Results.Content(ClientHtmlRenderer.RenderDocument(shellHtml), "text/html");
    }

    private static IResult HandleCallback(
        HttpContext context,
        ConcurrentDictionary<string, TaskCompletionSource<AuthorizationResult>> pending,
        ClientSessionState session)
    {
        var query = context.Request.Query;
        var state = query["state"].ToString();

        if (!pending.TryRemove(state, out var tcs))
        {
            session.AppendLog($"Received a callback with an unrecognized state ('{state}') - ignoring it.");
            return Results.Redirect("/");
        }

        var error = query["error"].ToString();
        if (!string.IsNullOrEmpty(error))
        {
            tcs.TrySetException(new InvalidOperationException($"Authorization failed: {error} - {query["error_description"]}"));
        }
        else
        {
            tcs.TrySetResult(new AuthorizationResult
            {
                Code = query["code"].ToString(),
                State = state,
                Iss = query["iss"] is { Count: > 0 } iss ? iss.ToString() : null,
            });
        }

        return Results.Redirect("/");
    }

    private static async Task RunConnectionAsync(
        ClientOptions options,
        ServerEndpointOptions serverEndpointOptions,
        ClientSessionState session,
        TaskCompletionSource<Uri> authorizationUriReady,
        CancellationToken cancellationToken)
    {
        try
        {
            var oauthOptions = new ClientOAuthOptions
            {
                RedirectUri = new Uri(options.RedirectUri),
                Scopes = options.Scopes,
                // The SDK auto-appends "offline_access" when the AS advertises it
                // (Keycloak always does). None of this Client's scenarios need a
                // refresh token, and Keycloak rejects the grant for users lacking
                // that realm role - drop it instead of trying to satisfy it. Always
                // logs the candidate scopes first, so what the WWW-Authenticate/PRM/
                // config actually requested is visible, not just the filtered result.
                ScopeSelector = candidateScopes =>
                {
                    var candidates = candidateScopes?.ToArray() ?? [];
                    session.AppendLog($"Scopes requested (WWW-Authenticate/PRM/config): {(candidates.Length == 0 ? "(none)" : string.Join(' ', candidates))}");
                    return candidates.Where(scope => scope != "offline_access");
                },
                AuthorizationCallbackHandler = (context, ct) =>
                    LoginFlows.HandleAuthorizationCallbackAsync(context, session, authorizationUriReady, ct),
                // Logs the token's claims (never the raw token) for manual
                // verification - see LoggingTokenCache/TokenInspector.
                TokenCache = new LoggingTokenCache(session),
            };

            if (options.UseCimd)
            {
                oauthOptions.ClientMetadataDocumentUri = new Uri(options.CimdDocumentUrl!);
            }
            else
            {
                oauthOptions.ClientId = options.ClientId;
                oauthOptions.ClientSecret = options.ClientSecret;
            }

            if (!string.IsNullOrWhiteSpace(options.Authority))
            {
                var authority = options.Authority;
                oauthOptions.AuthServerSelector = servers => AuthServerSelector.Select(servers, authority, session.AppendLog);
            }

            // HttpDiagnosticsHandler makes the 401/WWW-Authenticate challenge and the
            // PRM fetch (and every other request this connection makes) visible in the
            // Log, so they can be corroborated independently instead of just inferred.
            var httpClient = new HttpClient(new HttpDiagnosticsHandler(session.AppendLog));

            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = "Target MCP server",
                    Endpoint = new Uri(serverEndpointOptions.Endpoint),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    ConnectionTimeout = TimeSpan.FromMinutes(5),
                    OAuth = oauthOptions,
                },
                httpClient,
                NullLoggerFactory.Instance,
                ownsHttpClient: true);

            var mcpClientOptions = new McpClientOptions
            {
                // Not HttpClientTransportOptions.ConnectionTimeout above - this is
                // what actually bounds the whole ConnectAsync call, interactive
                // login included. Its default is far too short for a human to
                // switch to the browser tab and log in.
                InitializationTimeout = TimeSpan.FromMinutes(5),
                // Pinning the protocol version (Keycloak scenario only, see
                // ServerEndpointOptions.ProtocolVersion) skips the SDK's newer-
                // protocol discovery probe, which otherwise re-triggers OAuth
                // mid-login against an already-auth-gated server.
                ProtocolVersion = serverEndpointOptions.ProtocolVersion,
            };

            var mcpClient = await McpClient.CreateAsync(transport, mcpClientOptions, cancellationToken: cancellationToken);
            session.AppendLog($"Connected to: {mcpClient.ServerInfo.Name}");

            var tools = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
            var toolSummaries = tools
                .Select(tool => (tool.Name, (string?)tool.Description, (string?)FormatSchema(tool.JsonSchema)))
                .ToList();
            foreach (var (toolName, description, _) in toolSummaries)
            {
                session.AppendLog($"Discovered tool: {toolName} - {description}");
            }

            await session.SetConnectedAsync(mcpClient, toolSummaries);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // User-initiated cancel, not a failure - back to NotConnected
            // so /connect can be retried immediately.
            session.AppendLog("Connection attempt cancelled.");
            session.SetStatus(ClientConnectionStatus.NotConnected);
            authorizationUriReady.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            session.AppendLog($"Connection failed: {ex.Message}");
            session.SetStatus(ClientConnectionStatus.Failed);
            authorizationUriReady.TrySetException(ex);
        }
    }

    private static async Task RunEmaConnectionAsync(
        ServerEndpointOptions serverEndpointOptions,
        EmaOptions emaOptions,
        ClientSessionState session,
        TaskCompletionSource<Uri> emaAuthorizationUriReady,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var emaHttpClient = httpClientFactory.CreateClient();
            var idJagProvider = new IdentityAssertionGrantProvider(
                new IdentityAssertionGrantProviderOptions
                {
                    // Step 3: exchange the subject id_token for an ID-JAG at Issuer.
                    // IdpClientId must be the *same* client_id that redeems the
                    // ID-JAG in step 4 below (ResourceClientId) - Keycloak checks
                    // the assertion's client_id claim against whichever client
                    // presents it. Issuer's own authorization policy is
                    // deliberately trivial (any validated subject is allowed), so
                    // no client secret is required for this step.
                    IdpUrl = emaOptions.IssuerUrl,
                    IdpTokenEndpoint = $"{emaOptions.IssuerUrl}/token",
                    IdpClientId = emaOptions.ResourceClientId,
                    // Step 4: redeem the ID-JAG at Keycloak via RFC 7523 JWT bearer
                    // grant, authenticating as the pre-registered confidential
                    // client Keycloak's identity-assertion-jwt feature requires.
                    ClientId = emaOptions.ResourceClientId,
                    ClientSecret = emaOptions.ResourceClientSecret,
                    TokenEndpointAuthMethod = "client_secret_post",
                    Scope = emaOptions.Scope,
                    IdTokenCallback = (idJagContext, ct) => LoginFlows.RunEmaLoginAsync(
                        emaHttpClient, emaOptions.IdentityProviderAuthority, emaOptions.RequireHttpsMetadata,
                        emaOptions.LoginClientId, emaOptions.LoginRedirectUri, session, emaAuthorizationUriReady, ct),
                },
                emaHttpClient,
                loggerFactory);

            var tokenContainer = await idJagProvider.GetAccessTokenAsync(
                resourceUrl: new Uri(serverEndpointOptions.Endpoint),
                authorizationServerUrl: new Uri(emaOptions.ResourceAuthority),
                cancellationToken: cancellationToken);

            // Unlike the primary leg (see LoggingTokenCache), the EMA leg's
            // redeemed access token doesn't flow through an ITokenCache - log/
            // capture its claims here instead, for the sidebar's claims panel.
            var emaClaims = TokenInspector.LogClaims(session, tokenContainer.AccessToken);
            if (emaClaims is not null)
            {
                session.SetLastEmaTokenClaims(emaClaims);
            }

            // Plain bearer call this time - no CIMD/OAuth-discovery needed, the
            // target server just validates the redeemed access token like any
            // other bearer request.
            var emaTransport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = "Target MCP server (EMA leg)",
                Endpoint = new Uri(serverEndpointOptions.Endpoint),
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromMinutes(5),
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {tokenContainer.AccessToken}",
                },
            });

            var mcpClientOptions = new McpClientOptions
            {
                InitializationTimeout = TimeSpan.FromMinutes(5),
                ProtocolVersion = serverEndpointOptions.ProtocolVersion,
            };

            var emaMcpClient = await McpClient.CreateAsync(emaTransport, mcpClientOptions, cancellationToken: cancellationToken);
            session.AppendLog($"EMA leg connected to: {emaMcpClient.ServerInfo.Name}");

            var tools = await emaMcpClient.ListToolsAsync(cancellationToken: cancellationToken);
            var toolSummaries = tools
                .Select(tool => (tool.Name, (string?)tool.Description, (string?)FormatSchema(tool.JsonSchema)))
                .ToList();
            foreach (var (toolName, description, _) in toolSummaries)
            {
                session.AppendLog($"EMA leg discovered tool: {toolName} - {description}");
            }

            await session.SetEmaConnectedAsync(emaMcpClient, toolSummaries);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            session.AppendLog("EMA connection attempt cancelled.");
            session.SetEmaStatus(ClientConnectionStatus.NotConnected);
            emaAuthorizationUriReady.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            session.AppendLog($"EMA connection failed: {ex.Message}");
            session.SetEmaStatus(ClientConnectionStatus.Failed);
            emaAuthorizationUriReady.TrySetException(ex);
        }
    }

    // Called first by every state-changing POST route - without it, a cross-site
    // form submission could drive this app's live MCP session. Every GET that
    // renders forms mints a fresh token via antiforgery.GetAndStoreTokens (see
    // RenderPageOrFragment); this validates it.
    private static async Task<bool> IsValidAntiforgeryTokenAsync(IAntiforgery antiforgery, HttpContext context, ClientSessionState session)
    {
        if (await antiforgery.IsRequestValidAsync(context))
        {
            return true;
        }

        session.AppendLog("Rejected a request with a missing or invalid anti-forgery token.");
        return false;
    }

    private static string DescribeContent(ContentBlock block) => block switch
    {
        TextContentBlock text => text.Text,
        _ => JsonSerializer.Serialize(block, McpJsonUtilities.DefaultOptions),
    };

    private static readonly JsonSerializerOptions PrettyPrint = new() { WriteIndented = true };

    // Shown next to each Invoke button so parameter names don't have to be guessed.
    private static string FormatSchema(JsonElement schema) =>
        JsonSerializer.Serialize(schema, PrettyPrint);

    /// <summary>
    /// Builds the CallToolAsync arguments dictionary from the Fields-mode
    /// form post: one plain-text input per schema property, converted per
    /// its declared type (object/array fields expect the user to type raw
    /// JSON directly into that field). Blank, non-required fields are
    /// omitted rather than sent as empty strings.
    /// </summary>
    private static Dictionary<string, object?> BuildArguments(IFormCollection form, List<ClientHtmlRenderer.SchemaField> fields)
    {
        var arguments = new Dictionary<string, object?>();
        foreach (var field in fields)
        {
            var raw = form[field.Name].ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            arguments[field.Name] = field.Type switch
            {
                "number" or "integer" when double.TryParse(raw, out var number) => number,
                "boolean" when bool.TryParse(raw, out var boolean) => boolean,
                "object" or "array" => TryParseJsonElement(raw) is { } parsed ? (object)parsed : raw,
                _ => raw,
            };
        }

        return arguments;
    }

    private static JsonElement? TryParseJsonElement(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
