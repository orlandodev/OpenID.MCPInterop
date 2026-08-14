using System.Text;
using System.Text.Json.Nodes;
using OpenID.MCPInterop.Common.Constants;

namespace OpenID.MCPInterop.Client;

/// <summary>Structured form of the claims <see cref="TokenInspector.LogClaims"/> decodes, for the UI's access-token panel (never the raw token itself). Public - exposed via <see cref="ClientSessionState.LastTokenClaims"/>.</summary>
public sealed record TokenClaims(string Typ, string Iss, string Sub, string Aud, string ClientId, DateTimeOffset? ExpiresAt);

/// <summary>
/// Decodes and logs an access token's claims for manual verification (RFC 9068
/// section 2.2: iss, sub, aud, exp, client_id) - never the raw token. Inspects
/// the JWT's unverified payload only; not a substitute for the resource
/// server's real signature/claims validation.
/// </summary>
internal static class TokenInspector
{
    /// <summary>Logs the token's claims and returns them structured (null if the token couldn't be decoded) so callers can also feed the UI's claims panel.</summary>
    public static TokenClaims? LogClaims(ClientSessionState session, string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            session.AppendLog("Access token is not a JWT (opaque token) - can't inspect claims.");
            return null;
        }

        try
        {
            var header = JsonNode.Parse(DecodeBase64UrlSegment(parts[0]));
            var payload = JsonNode.Parse(DecodeBase64UrlSegment(parts[1]));

            var typ = header?["typ"]?.GetValue<string>() ?? "(none)";
            var iss = payload?["iss"]?.GetValue<string>() ?? "(missing)";
            var sub = payload?["sub"]?.GetValue<string>() ?? "(missing)";
            var aud = payload?["aud"]?.ToJsonString() ?? "(missing)";
            var clientId = payload?[OAuthConstants.ClientIdClaim]?.GetValue<string>() ?? payload?["azp"]?.GetValue<string>() ?? "(missing)";
            var expiresAt = payload?["exp"]?.GetValue<long>() is { } exp
                ? DateTimeOffset.FromUnixTimeSeconds(exp)
                : (DateTimeOffset?)null;

            var claims = new TokenClaims(typ, iss, sub, aud, clientId, expiresAt);
            session.AppendLog($"Access token claims - typ:{typ} iss:{iss} sub:{sub} aud:{aud} client_id/azp:{clientId} exp:{expiresAt?.ToString("u") ?? "(missing)"}");
            return claims;
        }
        catch (Exception ex)
        {
            session.AppendLog($"Could not decode access token claims: {ex.Message}");
            return null;
        }
    }

    private static string DecodeBase64UrlSegment(string segment)
    {
        var base64 = segment.Replace('-', '+').Replace('_', '/');
        var padding = (base64.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };
        base64 += padding;

        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }
}
