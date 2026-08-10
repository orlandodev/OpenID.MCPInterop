using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace OpenID.MCPInterop.Client.Partner;

/// <summary>
/// Maps this project's routes: the "very simple UI" (home page, connect,
/// invoke) plus the loopback OAuth callback the direct-trust leg's
/// authorization-code+PKCE flow redirects back to. See LoginFlow.cs for why
/// /connect redirects the requesting browser itself instead of opening a new
/// tab like Client's console-oriented LoginFlows does.
/// </summary>
public static class Endpoints
{
    public static WebApplication MapPartnerEndpoints(this WebApplication app)
    {
        app.MapGet("/", (HttpContext context, IAntiforgery antiforgery, PartnerSessionState session) =>
        {
            var antiforgeryTokens = antiforgery.GetAndStoreTokens(context);
            return Results.Content(RenderHomePage(session, antiforgeryTokens), "text/html");
        });

        app.MapPost("/connect", async (HttpContext context, IAntiforgery antiforgery, PartnerSessionState session, PartnerOptions options) =>
        {
            if (!await IsValidAntiforgeryTokenAsync(antiforgery, context, session))
            {
                return Results.BadRequest("Invalid or missing anti-forgery token.");
            }

            if (!session.TryBeginConnecting(out var cancellationToken))
            {
                return Results.Redirect("/");
            }

            session.AppendLog($"Connecting to {options.TargetServerEndpoint}...");

            var authorizationUriReady = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

            _ = Task.Run(() => RunConnectionAsync(options, session, authorizationUriReady, cancellationToken));

            try
            {
                // Waits on the PRM/discovery round trip, not on the human login.
                var authorizationUri = await authorizationUriReady.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                return Results.Redirect(authorizationUri.ToString());
            }
            catch (Exception ex)
            {
                // RunConnectionAsync's catch already logs/sets Status for anything
                // it throws directly - only ReportTimeout is new here.
                if (ConnectFailureReporter.Classify(ex) == ConnectFailureAction.ReportTimeout)
                {
                    session.AppendLog("Could not start login: timed out waiting for the authorization URL.");
                    session.SetStatus(PartnerConnectionStatus.Failed);
                }

                return Results.Redirect("/");
            }
        });

        app.MapPost("/cancel", async (HttpContext context, IAntiforgery antiforgery, PartnerSessionState session) =>
        {
            if (!await IsValidAntiforgeryTokenAsync(antiforgery, context, session))
            {
                return Results.BadRequest("Invalid or missing anti-forgery token.");
            }

            // Lets a stuck Connecting attempt be abandoned and retried
            // without restarting this process.
            session.CancelConnecting();
            return Results.Redirect("/");
        });

        app.MapGet("/callback", (HttpContext context, PartnerSessionState session) =>
        {
            var query = context.Request.Query;
            var state = query["state"].ToString();

            if (!session.PendingCallbacks.TryRemove(state, out var tcs))
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
        });

        app.MapPost("/tools/{name}/invoke", async (string name, HttpContext context, IAntiforgery antiforgery, PartnerSessionState session) =>
        {
            if (!await IsValidAntiforgeryTokenAsync(antiforgery, context, session))
            {
                return Results.BadRequest("Invalid or missing anti-forgery token.");
            }

            if (session.Client is not { } client)
            {
                session.AppendLog("Cannot invoke a tool before connecting.");
                return Results.Redirect("/");
            }

            var form = await context.Request.ReadFormAsync();
            var argumentsJson = form["arguments"].ToString();
            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                argumentsJson = "{}";
            }

            Dictionary<string, object?> arguments;
            try
            {
                // Tool schemas aren't known ahead of time, so arguments are freeform
                // JSON - object? boxes each value as a JsonElement, which CallToolAsync
                // re-serializes correctly regardless of shape.
                arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson) ?? [];
            }
            catch (JsonException ex)
            {
                session.AppendLog($"Tool '{name}' not invoked - arguments must be valid JSON: {ex.Message}");
                return Results.Redirect("/");
            }

            // Bounds the wait so a tool call that never responds (e.g. a stuck
            // external control-plane check) fails predictably instead of hanging.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            try
            {
                var result = await client.CallToolAsync(name, arguments, cancellationToken: timeoutCts.Token);

                // Tool failures come back as a normal result with IsError true
                // (per MCP spec), not a thrown exception - and content isn't
                // always plain text, so both need surfacing here.
                var contentSummary = result.Content.Count == 0
                    ? "(no content)"
                    : string.Join(" | ", result.Content.Select(DescribeContent));
                var structuredSummary = result.StructuredContent is { } structured
                    ? $" structuredContent: {structured.GetRawText()}"
                    : string.Empty;
                var outcome = result.IsError == true ? "ERROR" : "result";
                session.AppendLog($"Tool '{name}' {outcome}: {contentSummary}{structuredSummary}");
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                session.AppendLog($"Tool '{name}' timed out after 30s with no response from the server.");
            }
            catch (Exception ex)
            {
                session.AppendLog($"Tool '{name}' failed: {ex.Message}");
            }

            return Results.Redirect("/");
        });

        return app;
    }

    private static async Task RunConnectionAsync(
        PartnerOptions options,
        PartnerSessionState session,
        TaskCompletionSource<Uri> authorizationUriReady,
        CancellationToken cancellationToken)
    {
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = "Partner MCP server",
                Endpoint = new Uri(options.TargetServerEndpoint),
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromMinutes(5),
                OAuth = new ClientOAuthOptions
                {
                    ClientId = options.ClientId,
                    ClientSecret = options.ClientSecret,
                    RedirectUri = new Uri(options.RedirectUri),
                    Scopes = options.Scopes,
                    // The SDK auto-appends "offline_access" when the AS advertises it
                    // (Keycloak always does). This app doesn't need a refresh token,
                    // and Keycloak rejects the grant for users lacking that realm role -
                    // drop it instead of trying to satisfy the grant.
                    ScopeSelector = candidateScopes => candidateScopes?.Where(scope => scope != "offline_access") ?? [],
                    // Prefer the AS we're actually registered against over the SDK's
                    // "first in the list" default; logs if that match fails.
                    AuthServerSelector = servers =>
                        PartnerAuthServerSelector.Select(servers, options.Authority, session.AppendLog),
                    AuthorizationCallbackHandler = (context, cancellationToken) =>
                        LoginFlow.HandleAuthorizationCallbackAsync(context, session, authorizationUriReady, cancellationToken),
                    // Logs the token's claims (never the raw token) for manual
                    // verification - see LoggingTokenCache/TokenInspector.
                    TokenCache = new LoggingTokenCache(session),
                },
            });

            var mcpClientOptions = new McpClientOptions
            {
                InitializationTimeout = TimeSpan.FromMinutes(5),
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
            session.SetStatus(PartnerConnectionStatus.NotConnected);
            authorizationUriReady.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            session.AppendLog($"Connection failed: {ex.Message}");
            session.SetStatus(PartnerConnectionStatus.Failed);
            authorizationUriReady.TrySetException(ex);
        }
    }

    // Called first by every state-changing POST route - without it, a cross-site
    // form submission could drive this app's live MCP session (see security
    // review: CSRF on /tools/{name}/invoke). GET / mints the token via
    // antiforgery.GetAndStoreTokens (see RenderHomePage); this validates it.
    private static async Task<bool> IsValidAntiforgeryTokenAsync(IAntiforgery antiforgery, HttpContext context, PartnerSessionState session)
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

    private static string RenderHomePage(PartnerSessionState session, AntiforgeryTokenSet antiforgeryTokens)
    {
        var antiforgeryField = $"""<input type="hidden" name="{WebUtility.HtmlEncode(antiforgeryTokens.FormFieldName)}" value="{WebUtility.HtmlEncode(antiforgeryTokens.RequestToken)}">""";

        var html = new StringBuilder();
        html.Append("""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <title>OpenID.MCPInterop.Client</title>
            """);

        if (session.Status == PartnerConnectionStatus.Connecting)
        {
            html.Append("""<meta http-equiv="refresh" content="2">""");
        }

        html.Append("""
            <style>
                body { font-family: sans-serif; max-width: 60rem; margin: 2rem auto; padding: 0 1rem; }
                .log { background: #111; color: #0f0; padding: 1rem; font-family: monospace; white-space: pre-wrap; max-height: 20rem; overflow-y: auto; }
                button { padding: 0.4rem 1rem; }
            </style>
            </head>
            <body>
            <h1>OpenID.MCPInterop.Client</h1>
            <p>Direct-trust leg - no CIMD, no ID-JAG. See docs/architecture.md.</p>
            """);

        html.Append($"<p><strong>Status:</strong> {WebUtility.HtmlEncode(session.Status.ToString())}</p>");

        if (session.Status is PartnerConnectionStatus.NotConnected or PartnerConnectionStatus.Failed)
        {
            html.Append($"""<form method="post" action="/connect">{antiforgeryField}<button type="submit">Connect</button></form>""");
        }

        if (session.Status == PartnerConnectionStatus.Connecting)
        {
            html.Append($"""
                <form method="post" action="/cancel">
                {antiforgeryField}
                <button type="submit">Cancel/Retry</button>
                <span>Cancelling and retrying is safe - no need to restart this app.</span>
                </form>
                """);
        }

        if (session.Tools.Count > 0)
        {
            html.Append("<h2>Tools</h2><ul>");
            foreach (var (name, description, schema) in session.Tools)
            {
                html.Append("<li>");
                html.Append($"<code>{WebUtility.HtmlEncode(name)}</code> - {WebUtility.HtmlEncode(description ?? string.Empty)}<br/>");
                if (!string.IsNullOrEmpty(schema))
                {
                    html.Append($"""<details><summary>Input schema</summary><pre>{WebUtility.HtmlEncode(schema)}</pre></details>""");
                }

                html.Append($"""<form method="post" action="/tools/{Uri.EscapeDataString(name)}/invoke">""");
                html.Append(antiforgeryField);
                html.Append("""<textarea name="arguments" rows="2" cols="60" placeholder="{}"></textarea><br/>""");
                html.Append("""<button type="submit">Invoke</button></form>""");
                html.Append("</li>");
            }

            html.Append("</ul>");
        }

        html.Append("<h2>Log</h2><div class=\"log\">");
        html.Append(WebUtility.HtmlEncode(string.Join('\n', session.Log)));
        html.Append("</div>");

        html.Append("</body></html>");

        return html.ToString();
    }
}
