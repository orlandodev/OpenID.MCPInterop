using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using OpenID.MCPInterop.Client;
using OpenID.MCPInterop.Client.Helpers;
using OpenID.MCPInterop.Client.Models;
using OpenID.MCPInterop.Client.Options;
using OpenID.MCPInterop.Client.State;

namespace OpenID.MCPInterop.Client.Rendering;

/// <summary>
/// Builds every HTML string this Client's web UI renders - the full document
/// shell and the <c>.mcp-app</c> fragment htmx swaps into it - kept separate
/// from <see cref="Endpoints"/> so routing/OAuth business logic isn't
/// interleaved with markup. Styled against the vendored
/// industry-styles.css/mcp-client.css (wwwroot/css) and lightly interactive
/// via htmx (wwwroot/lib) plus wwwroot/js/mcp-client.js.
/// </summary>
internal static class ClientHtmlRenderer
{
    /// <summary>The full HTML document - doctype, head (styles, htmx, the app's own script), and the already-rendered <c>.mcp-app</c> shell.</summary>
    internal static string RenderDocument(string shellHtml) => $$"""
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>OpenID.MCPInterop.Client</title>
            <link rel="stylesheet" href="/css/industry-styles.css">
            <link rel="stylesheet" href="/css/mcp-client.css">
            <script src="/lib/htmx.min.js"></script>
        </head>
        <body>
        {{shellHtml}}
        <script src="/js/mcp-client.js"></script>
        </body>
        </html>
        """;

    /// <summary>A tool input parameter parsed out of its JSON schema, for the Fields-mode form.</summary>
    internal readonly record struct SchemaField(
        string Name,
        string Type,
        bool Required,
        string? Description = null,
        IReadOnlyList<string>? EnumValues = null,
        string? DefaultValue = null);

    /// <summary>
    /// Reads a tool's JSON schema (already stored pretty-printed, from
    /// <see cref="Endpoints.FormatSchema"/>) back into a flat field list for
    /// the Fields-mode input form - name, effective JSON-Schema <c>type</c>,
    /// whether it's in the schema's <c>required</c> array, plus the
    /// <c>description</c>/<c>enum</c>/<c>default</c> hints. Shared with
    /// <see cref="ToolArgumentBuilder"/>, which needs the same field shape to
    /// validate submitted form values against the schema.
    /// </summary>
    internal static List<SchemaField> ParseSchemaFields(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            var root = document.RootElement;

            var required = new HashSet<string>();
            if (root.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in requiredElement.EnumerateArray())
                {
                    if (item.GetString() is { } name)
                    {
                        required.Add(name);
                    }
                }
            }

            var fields = new List<SchemaField>();
            if (root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    var schema = property.Value;
                    var enumValues = ReadEnumValues(schema);
                    fields.Add(new SchemaField(
                        property.Name,
                        ResolveType(schema, enumValues),
                        required.Contains(property.Name),
                        ReadString(schema, "description") ?? ReadString(schema, "title"),
                        enumValues,
                        schema.TryGetProperty("default", out var defaultElement) ? JsonText(defaultElement) : null));
                }
            }

            return fields;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// A property's single effective type. Handles the shapes schema
    /// generators commonly emit instead of a plain <c>"type": "x"</c>:
    /// nullable unions (<c>["string", "null"]</c>), <c>anyOf</c>/<c>oneOf</c>
    /// wrappers, and type-less schemas implied by <c>enum</c>,
    /// <c>properties</c> or <c>items</c>.
    /// </summary>
    private static string ResolveType(JsonElement schema, IReadOnlyList<string>? enumValues)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return "string";
        }

        if (schema.TryGetProperty("type", out var typeElement))
        {
            if (typeElement.ValueKind == JsonValueKind.String)
            {
                return typeElement.GetString() ?? "string";
            }

            if (typeElement.ValueKind == JsonValueKind.Array)
            {
                var nonNull = typeElement.EnumerateArray()
                    .Select(item => item.GetString())
                    .FirstOrDefault(item => item is not null and not "null");
                return nonNull ?? "string";
            }
        }

        foreach (var combinator in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (schema.TryGetProperty(combinator, out var branches) && branches.ValueKind == JsonValueKind.Array)
            {
                var branchType = branches.EnumerateArray()
                    .Select(branch => ResolveType(branch, ReadEnumValues(branch)))
                    .FirstOrDefault(type => type != "null");
                if (branchType is not null)
                {
                    return branchType;
                }
            }
        }

        if (enumValues is { Count: > 0 } && schema.TryGetProperty("enum", out var enumElement))
        {
            return enumElement.EnumerateArray().First().ValueKind switch
            {
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                _ => "string",
            };
        }

        if (schema.TryGetProperty("properties", out _))
        {
            return "object";
        }

        return schema.TryGetProperty("items", out _) ? "array" : "string";
    }

    private static IReadOnlyList<string>? ReadEnumValues(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("enum", out var enumElement)
            || enumElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = enumElement.EnumerateArray()
            .Where(item => item.ValueKind != JsonValueKind.Null)
            .Select(JsonText)
            .ToList();
        return values.Count > 0 ? values : null;
    }

    private static string? ReadString(JsonElement schema, string propertyName) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string JsonText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText(),
    };

    private static string BuildAntiforgeryField(AntiforgeryTokenSet tokens) =>
        $"""<input type="hidden" name="{WebUtility.HtmlEncode(tokens.FormFieldName)}" value="{WebUtility.HtmlEncode(tokens.RequestToken)}">""";

    /// <summary>Renders the <c>.mcp-app</c> shell - sidebar (server list + claims panel) and main (header, tool list/detail, console) - matching mcp-client.css's reference markup skeleton.</summary>
    internal static string RenderAppShell(
        ClientSessionState session,
        ClientOptions options,
        ServerEndpointOptions serverEndpointOptions,
        EmaOptions? emaOptions,
        AntiforgeryTokenSet antiforgeryTokens)
    {
        var antiforgeryField = BuildAntiforgeryField(antiforgeryTokens);
        var selectedLeg = Endpoints.NormalizeLeg(session.SelectedLeg, options);

        // Only while a connection attempt is outstanding does this app poll
        // itself (via the same GET /servers/{leg} route a sidebar click
        // would hit) - the moment /connect kicks off, the browser navigates
        // away to the external IdP anyway, so nothing here is watched until
        // the human comes back through /callback and lands on a possibly-
        // still-Connecting page.
        var isConnecting = session.Status == ClientConnectionStatus.Connecting || session.EmaStatus == ClientConnectionStatus.Connecting;
        var pollAttributes = isConnecting
            ? $"""hx-get="/servers/{selectedLeg}" hx-trigger="every 2s" hx-swap="outerHTML" """
            : string.Empty;

        var html = new StringBuilder();
        html.Append($"""<div class="mcp-app" id="mcp-app" {pollAttributes}>""");
        html.Append(RenderSidebar(session, options, serverEndpointOptions, selectedLeg, antiforgeryField));
        html.Append(RenderMain(session, options, serverEndpointOptions, emaOptions, selectedLeg, antiforgeryField));
        html.Append("</div>");
        return html.ToString();
    }

    private static string RenderSidebar(
        ClientSessionState session,
        ClientOptions options,
        ServerEndpointOptions serverEndpointOptions,
        string selectedLeg,
        string antiforgeryField)
    {
        var html = new StringBuilder();
        html.Append("""
            <aside class="mcp-sidebar">
              <div class="mcp-brand">
                <span class="mcp-brand-mark"></span>
                <span class="mcp-brand-name">MCPInterop</span>
                <span class="mcp-brand-version mcp-eyebrow">v0.1</span>
              </div>
            """);

        var legCount = options.UseEma ? 2 : 1;
        html.Append($"""<div class="mcp-section-head"><h6>Servers</h6><span class="mcp-count">{legCount} known</span></div>""");

        html.Append("""<div class="mcp-server-list">""");
        html.Append(RenderServerRow("primary", session, selectedLeg, serverEndpointOptions));
        if (options.UseEma)
        {
            html.Append(RenderServerRow("ema", session, selectedLeg, serverEndpointOptions));
        }

        html.Append("</div>");

        var claims = selectedLeg == "ema" ? session.LastEmaTokenClaims : session.LastTokenClaims;
        html.Append($"""<div class="mcp-sidebar-foot">{RenderClaimsPanel(claims)}</div>""");
        html.Append("</aside>");
        return html.ToString();
    }

    private static string RenderServerRow(string leg, ClientSessionState session, string selectedLeg, ServerEndpointOptions serverEndpointOptions)
    {
        var status = leg == "ema" ? session.EmaStatus : session.Status;
        var client = leg == "ema" ? session.EmaClient : session.Client;
        var name = client?.TryGetServerName() ?? (leg == "ema" ? "EMA leg" : "Primary leg");
        var dotClass = status switch
        {
            ClientConnectionStatus.Connected => "mcp-dot is-live",
            ClientConnectionStatus.Connecting => "mcp-dot",
            _ => "mcp-dot is-off",
        };
        var current = leg == selectedLeg ? "true" : "false";

        return $"""
            <a class="mcp-server" href="/servers/{leg}" hx-get="/servers/{leg}" hx-target="#mcp-app" hx-swap="outerHTML" hx-push-url="true" aria-current="{current}">
              <span class="mcp-server-name"><span class="{dotClass}"></span>{WebUtility.HtmlEncode(name)}</span>
              <span class="mcp-server-endpoint">{WebUtility.HtmlEncode(serverEndpointOptions.Endpoint)}</span>
            </a>
            """;
    }

    private static string RenderClaimsPanel(TokenClaims? claims)
    {
        if (claims is null)
        {
            return string.Empty;
        }

        var ttl = claims.ExpiresAt is { } expiresAt
            ? FormatTimeUntil(expiresAt)
            : "unknown";

        return $"""
            <div class="blueprint mcp-panel">
              <i class="corner tl"></i><i class="corner tr"></i><i class="corner bl"></i><i class="corner br"></i>
              <div class="mcp-panel-head"><span class="mcp-eyebrow">Access token</span><span class="tag tag-accent">{WebUtility.HtmlEncode(claims.Typ)}</span></div>
              <dl class="mcp-kv mcp-kv--mono-key">
                <dt>iss</dt><dd>{WebUtility.HtmlEncode(claims.Iss)}</dd>
                <dt>sub</dt><dd>{WebUtility.HtmlEncode(claims.Sub)}</dd>
                <dt>aud</dt><dd>{WebUtility.HtmlEncode(claims.Aud)}</dd>
                <dt>azp</dt><dd>{WebUtility.HtmlEncode(claims.ClientId)}</dd>
              </dl>
              <div class="mcp-panel-foot">Expires {WebUtility.HtmlEncode(ttl)}</div>
            </div>
            """;
    }

    private static string FormatTimeUntil(DateTimeOffset at)
    {
        var remaining = at - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "expired";
        }

        return remaining.TotalHours >= 1
            ? $"in {(int)remaining.TotalHours}h {remaining.Minutes}m"
            : $"in {(int)remaining.TotalMinutes}m";
    }

    private static string RenderMain(
        ClientSessionState session,
        ClientOptions options,
        ServerEndpointOptions serverEndpointOptions,
        EmaOptions? emaOptions,
        string selectedLeg,
        string antiforgeryField)
    {
        var html = new StringBuilder();
        html.Append("""<main class="mcp-main">""");
        html.Append(RenderHeader(session, options, serverEndpointOptions, selectedLeg, antiforgeryField));
        html.Append("""<div class="mcp-body"><section class="mcp-work">""");
        html.Append(RenderToolList(session, selectedLeg));
        html.Append(RenderDetail(session, selectedLeg, antiforgeryField));
        html.Append("</section>");
        html.Append(RenderConsole(session, antiforgeryField));
        html.Append("</div></main>");
        return html.ToString();
    }

    private static string RenderHeader(
        ClientSessionState session,
        ClientOptions options,
        ServerEndpointOptions serverEndpointOptions,
        string selectedLeg,
        string antiforgeryField)
    {
        var status = selectedLeg == "ema" ? session.EmaStatus : session.Status;
        var client = selectedLeg == "ema" ? session.EmaClient : session.Client;
        var name = client?.TryGetServerName() ?? (selectedLeg == "ema" ? "EMA leg" : "Primary leg");

        var statusTag = status == ClientConnectionStatus.Connected
            ? $"""<span class="tag tag-accent"><span class="mcp-dot is-live"></span>{status}</span>"""
            : $"""<span class="tag tag-neutral">{status}</span>""";

        var trustTag = selectedLeg == "ema" ? "ID-JAG" : (options.UseCimd ? "CIMD" : "direct-trust");

        var metaText = selectedLeg == "ema"
            ? "ID-JAG leg - RFC 8693 token exchange + RFC 7523 JWT bearer redemption"
            : $"{(options.UseCimd ? "CIMD client_id" : "no CIMD")} · {(options.UseEma ? "ID-JAG available" : "no ID-JAG")}";

        var html = new StringBuilder();
        html.Append($"""
            <header class="mcp-header">
              <div class="mcp-header-id">
                <div class="mcp-header-title">
                  <h4>{WebUtility.HtmlEncode(name)}</h4>
                  {statusTag}
                  <span class="tag tag-outline">{WebUtility.HtmlEncode(trustTag)}</span>
                </div>
                <div class="mcp-header-meta">{WebUtility.HtmlEncode(serverEndpointOptions.Endpoint)} · {WebUtility.HtmlEncode(metaText)}</div>
              </div>
              <div class="mcp-header-actions">
            """);

        var connectAction = selectedLeg == "ema" ? "/ema-connect" : "/connect";
        var cancelAction = selectedLeg == "ema" ? "/ema-cancel" : "/cancel";
        var connectLabel = selectedLeg == "ema" ? "Start EMA leg" : "Connect";

        if (selectedLeg == "ema" && session.Status != ClientConnectionStatus.Connected)
        {
            html.Append("""<span class="mcp-count">Connect the primary leg first</span>""");
        }
        else if (status is ClientConnectionStatus.NotConnected or ClientConnectionStatus.Failed)
        {
            html.Append($"""<form method="post" action="{connectAction}">{antiforgeryField}<button type="submit" class="btn btn-secondary">{connectLabel}</button></form>""");
        }
        else if (status == ClientConnectionStatus.Connecting)
        {
            html.Append($"""<form method="post" action="{cancelAction}">{antiforgeryField}<button type="submit" class="btn btn-secondary">Cancel/Retry</button></form>""");
        }

        html.Append("</div></header>");
        return html.ToString();
    }

    private static string RenderToolList(ClientSessionState session, string selectedLeg)
    {
        var tools = selectedLeg == "ema" ? session.EmaTools : session.Tools;
        var selectedName = SelectedToolName(session, selectedLeg, tools);

        var html = new StringBuilder();
        html.Append($"""
            <div class="mcp-toollist">
              <div class="mcp-toollist-head"><h6>Tools</h6><span class="mcp-count">{tools.Count} discovered</span></div>
              <div class="mcp-toollist-scroll">
            """);

        if (tools.Count == 0)
        {
            html.Append("""<p class="text-muted">No tools discovered yet - connect this leg to run discovery.</p>""");
        }

        foreach (var tool in tools)
        {
            var current = tool.Name == selectedName ? "true" : "false";
            html.Append($"""
                <a class="mcp-tool" href="/tools/{selectedLeg}/{Uri.EscapeDataString(tool.Name)}" hx-get="/tools/{selectedLeg}/{Uri.EscapeDataString(tool.Name)}" hx-target="#mcp-app" hx-swap="outerHTML" hx-push-url="true" aria-current="{current}">
                  <span class="mcp-tool-row"><span class="mcp-tool-name">{WebUtility.HtmlEncode(tool.Name)}</span></span>
                  <span class="mcp-tool-blurb">{WebUtility.HtmlEncode(tool.Description ?? string.Empty)}</span>
                </a>
                """);
        }

        html.Append("</div></div>");
        return html.ToString();
    }

    private static string? SelectedToolName(ClientSessionState session, string leg, IReadOnlyList<(string Name, string? Description, string? Schema)> tools)
    {
        var explicitSelection = leg == "ema" ? session.SelectedEmaToolName : session.SelectedToolName;
        if (explicitSelection is not null && tools.Any(t => t.Name == explicitSelection))
        {
            return explicitSelection;
        }

        return tools.Count > 0 ? tools[0].Name : null;
    }

    private static string RenderDetail(ClientSessionState session, string selectedLeg, string antiforgeryField)
    {
        var tools = selectedLeg == "ema" ? session.EmaTools : session.Tools;
        var selectedName = SelectedToolName(session, selectedLeg, tools);
        var tool = tools.FirstOrDefault(t => t.Name == selectedName);

        if (tool.Name is null)
        {
            return """
                <div class="mcp-detail" id="mcp-detail">
                  <div class="mcp-detail-col">
                    <p class="text-muted">This leg isn't connected, or has no tools discovered yet.</p>
                  </div>
                </div>
                """;
        }

        var fields = ParseSchemaFields(tool.Schema);
        var html = new StringBuilder();
        html.Append($"""
            <div class="mcp-detail" id="mcp-detail">
              <div class="mcp-detail-col">
                <div>
                  <div class="mcp-tool-title"><h3>{WebUtility.HtmlEncode(tool.Name)}</h3></div>
                  <p class="mcp-tool-desc">{WebUtility.HtmlEncode(tool.Description ?? string.Empty)}</p>
                </div>

                <div class="blueprint mcp-card">
                  <i class="corner tl"></i><i class="corner tr"></i><i class="corner bl"></i><i class="corner br"></i>
                  <div class="mcp-card-head">
                    <h6>Input</h6>
                    <div class="mcp-toggle" id="mcp-input-toggle">
                      <button type="button" data-mode="form" aria-pressed="true" onclick="mcpSetMode('form')">Fields</button>
                      <button type="button" data-mode="json" aria-pressed="false" onclick="mcpSetMode('json')">JSON</button>
                      <button type="button" data-mode="schema" aria-pressed="false" onclick="mcpSetMode('schema')">Schema</button>
                    </div>
                  </div>

                  <form method="post" action="/tools/{selectedLeg}/{Uri.EscapeDataString(tool.Name)}/invoke"
                        hx-post="/tools/{selectedLeg}/{Uri.EscapeDataString(tool.Name)}/invoke" hx-target="#mcp-app" hx-swap="outerHTML">
                    {antiforgeryField}
                    <div class="mcp-params" id="mcp-fields-view">
            """);

        if (fields.Count == 0)
        {
            html.Append("""<p class="text-muted">This tool takes no input.</p>""");
        }

        foreach (var field in fields)
        {
            html.Append(RenderField(field));
        }

        html.Append($"""
                    </div>
                    <div id="mcp-json-view" style="display:none">
                      <pre class="mcp-code" id="mcp-json-preview">{"{}"}</pre>
                    </div>
                    <div id="mcp-schema-view" style="display:none">
                      <pre class="mcp-code">{WebUtility.HtmlEncode(tool.Schema ?? "(no input schema published)")}</pre>
                    </div>

                    <div class="mcp-card-actions">
                      <button type="submit" class="btn btn-primary">Invoke</button>
            """);
        html.Append($"""<span class="mcp-endpoint-hint">POST /tools/{selectedLeg}/{Uri.EscapeDataString(tool.Name)}/invoke</span>""");
        html.Append("""
                    </div>
                  </form>
                </div>
            """);

        var lastResult = selectedLeg == "ema" ? session.LastEmaResult : session.LastResult;
        if (lastResult is not null)
        {
            html.Append(RenderResultCard(selectedLeg, lastResult));
        }

        html.Append("</div></div>");
        return html.ToString();
    }

    /// <summary>
    /// One Fields-mode input, shaped by its schema so an invalid value is hard
    /// to enter: enums and booleans become dropdowns, numbers get numeric
    /// inputs, object/array fields get a wide JSON textarea, and required
    /// fields carry <c>required</c> so the browser blocks an empty submit.
    /// </summary>
    private static string RenderField(SchemaField field)
    {
        var name = WebUtility.HtmlEncode(field.Name);
        var type = WebUtility.HtmlEncode(field.Type);
        var requiredMark = field.Required ? """<span class="mcp-param-req">*</span>""" : string.Empty;
        var requiredAttribute = field.Required ? " required" : string.Empty;
        var commonAttributes = $"""name="{name}" data-field-name="{name}" data-field-type="{type}"{requiredAttribute}""";
        var placeholder = WebUtility.HtmlEncode(field.DefaultValue is null ? field.Type : $"default: {field.DefaultValue}");
        var isWide = field.Type is "object" or "array";

        var options = field.EnumValues ?? (field.Type == "boolean" ? ["true", "false"] : null);
        var input = options switch
        {
            not null => RenderSelect(commonAttributes, options, field.DefaultValue, field.Required),
            _ when isWide => $"""<textarea class="input" rows="4" {commonAttributes} placeholder="{WebUtility.HtmlEncode(field.DefaultValue ?? $"JSON {field.Type} - see Schema")}"></textarea>""",
            _ when field.Type == "integer" => $"""<input class="input" type="number" step="1" {commonAttributes} placeholder="{placeholder}">""",
            _ when field.Type == "number" => $"""<input class="input" type="number" step="any" {commonAttributes} placeholder="{placeholder}">""",
            _ => $"""<input class="input" {commonAttributes} placeholder="{placeholder}">""",
        };

        var description = field.Description is null
            ? string.Empty
            : $"""<small class="mcp-param-desc">{WebUtility.HtmlEncode(field.Description)}</small>""";

        return $"""
            <div class="field{(isWide ? " is-wide" : string.Empty)}">
              <label>{name}{requiredMark} <span class="mcp-param-type">{type}</span></label>
              {input}
              {description}
            </div>
            """;
    }

    private static string RenderSelect(string commonAttributes, IReadOnlyList<string> options, string? defaultValue, bool required)
    {
        var html = new StringBuilder($"""<select class="input" {commonAttributes}>""");
        var blankLabel = required ? "- choose -" : "(not set)";
        html.Append($"""<option value="">{blankLabel}</option>""");
        foreach (var option in options)
        {
            var selected = option == defaultValue ? " selected" : string.Empty;
            var encoded = WebUtility.HtmlEncode(option);
            html.Append($"""<option value="{encoded}"{selected}>{encoded}</option>""");
        }

        html.Append("</select>");
        return html.ToString();
    }

    private static string RenderResultCard(string leg, ToolInvocationResult result)
    {
        var verdictClass = result.IsError ? "mcp-verdict is-deny" : "mcp-verdict";
        var verdictWord = result.IsError ? "ERROR" : "OK";

        return $"""
            <div class="blueprint mcp-decision">
              <i class="corner tl"></i><i class="corner tr"></i><i class="corner bl"></i><i class="corner br"></i>
              <div class="{verdictClass}">
                <span class="mcp-verdict-word">{verdictWord}</span>
                <span class="mcp-verdict-reason">{WebUtility.HtmlEncode(leg)} leg · {WebUtility.HtmlEncode(result.ToolName)}</span>
                <span class="mcp-verdict-latency">{(int)result.Elapsed.TotalMilliseconds} ms</span>
              </div>
              <div class="mcp-decision-grid">
                <div class="mcp-decision-row"><span class="mcp-decision-key">leg</span><span class="mcp-decision-val">{WebUtility.HtmlEncode(leg)}</span></div>
                <div class="mcp-decision-row"><span class="mcp-decision-key">tool</span><span class="mcp-decision-val">{WebUtility.HtmlEncode(result.ToolName)}</span></div>
                <div class="mcp-decision-row"><span class="mcp-decision-key">at</span><span class="mcp-decision-val">{result.At.UtcDateTime:yyyy-MM-ddTHH:mm:ss'Z'}</span></div>
              </div>
              <div class="mcp-result">
                <span class="mcp-eyebrow">Tool result</span>
                <pre class="mcp-code">{WebUtility.HtmlEncode(result.ResultBody)}</pre>
              </div>
            </div>
            """;
    }

    private static string RenderConsole(ClientSessionState session, string antiforgeryField)
    {
        var log = session.Log;
        var html = new StringBuilder();
        html.Append($"""
            <section class="mcp-console" id="mcp-console">
              <div class="mcp-console-resize-handle" title="Drag to resize"></div>
              <div class="mcp-console-bar">
                <span class="mcp-console-label">Log</span>
                <span class="mcp-dot is-live"></span>
                <span class="mcp-console-count">{log.Count} events</span>
                <div class="mcp-console-actions">
                  <button type="button" onclick="mcpCopyLog(this)">Copy</button>
                  <button type="button" onclick="mcpToggleConsole(this)">Collapse</button>
                  <form method="post" action="/log/clear" hx-post="/log/clear" hx-target="#mcp-app" hx-swap="outerHTML" style="display:contents">
                    {antiforgeryField}
                    <button type="submit">Clear</button>
                  </form>
                </div>
              </div>
              <div class="mcp-console-body">
            """);

        foreach (var (timestamp, message) in log)
        {
            html.Append($"""<div class="mcp-log-line"><span class="mcp-log-time">{timestamp.UtcDateTime:yyyy-MM-ddTHH:mm:ss'Z'}</span><span class="mcp-log-text">{WebUtility.HtmlEncode(message)}</span></div>""");
        }

        html.Append("</div></section>");
        return html.ToString();
    }
}
