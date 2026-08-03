using System.Collections.Concurrent;
using ModelContextProtocol.Authentication;
using OpenID.MCPInterop.Common.Models;

namespace OpenID.MCPInterop.Client;

public static class Endpoints
{
    /// <summary>
    /// Maps the routes this Client hosts alongside its outbound MCP/OAuth
    /// work: the CIMD document itself, and the loopback callback targets for
    /// both the CIMD leg's (SDK-driven) and the EMA leg's (hand-rolled)
    /// authorization-code+PKCE flows.
    /// </summary>
    public static WebApplication MapClientEndpoints(
        this WebApplication app,
        CimdMetadataDocument cimdDocument,
        ConcurrentDictionary<string, TaskCompletionSource<AuthorizationResult>> pendingAuthorizations,
        ConcurrentDictionary<string, TaskCompletionSource<AuthorizationResult>> pendingEmaAuthorizations)
    {
        // Hosts this Client's CIMD document - the client_id it presents to Keycloak
        // *is* this URL, per draft-ietf-oauth-client-id-metadata-document. Keycloak
        // fetches it server-to-server the first time it sees this client_id at the
        // authorization endpoint (see Client:CimdDocumentUrl in appsettings.json for
        // the Docker networking note).
        app.MapGet("/client-metadata.json", () => Results.Json(cimdDocument));

        // Loopback redirect target for the Agent Governance (CIMD) leg's
        // authorization code + PKCE flow, driven by the MCP SDK itself.
        app.MapGet("/callback", (HttpContext context) => HandleCallback(
            context, pendingAuthorizations, "Authorization complete - you can close this window and return to the console."));

        // Loopback redirect target for the EMA leg's dedicated, hand-rolled
        // authorization code + PKCE flow (arrow 1 in the architecture diagram: user
        // authenticates to their own IdP to obtain a subject id_token).
        app.MapGet("/ema-callback", (HttpContext context) => HandleCallback(
            context, pendingEmaAuthorizations, "EMA login complete - you can close this window and return to the console."));

        return app;
    }

    private static IResult HandleCallback(
        HttpContext context,
        ConcurrentDictionary<string, TaskCompletionSource<AuthorizationResult>> pending,
        string completionMessage)
    {
        var query = context.Request.Query;
        var state = query["state"].ToString();

        if (!pending.TryRemove(state, out var tcs))
        {
            // A blank/unrecognized state means no waiting login owns this
            // callback - previously this fell through to the same "success"
            // page below with nothing actually completed, silently hanging
            // whichever login really is still pending until it times out.
            // Say so instead, both to the browser and the console.
            Console.WriteLine($"Received a callback with an unrecognized state ('{state}') - ignoring it.");
            return Results.Content(
                "This callback's state didn't match a pending login. If you have another login window open, use that one instead.",
                "text/html");
        }

        var error = query["error"].ToString();
        if (!string.IsNullOrEmpty(error))
        {
            tcs.TrySetException(new InvalidOperationException(
                $"Authorization failed: {error} - {query["error_description"]}"));
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

        return Results.Content(completionMessage, "text/html");
    }
}
