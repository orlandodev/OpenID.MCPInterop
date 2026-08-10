namespace OpenID.MCPInterop.Client.Partner;

/// <summary>
/// Picks an authorization server out of a target server's advertised list, for
/// <see cref="ModelContextProtocol.Authentication.ClientOAuthOptions.AuthServerSelector"/>.
/// Prefers the AS matching <c>configuredAuthority</c> (trailing-slash/case-
/// insensitive) over the SDK's "first in the list" default, and reports via
/// <c>onFallback</c> when no match is found instead of silently connecting
/// through the wrong AS (see review).
/// </summary>
internal static class PartnerAuthServerSelector
{
    public static Uri? Select(IReadOnlyList<Uri> servers, string configuredAuthority, Action<string> onFallback)
    {
        var match = servers.FirstOrDefault(server => string.Equals(
            server.ToString().TrimEnd('/'), configuredAuthority.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            return match;
        }

        var fallback = servers.FirstOrDefault();
        onFallback(fallback is null
            ? $"No authorization server advertised by the target server matched the configured Authority ('{configuredAuthority}'), and none were advertised at all."
            : $"No authorization server advertised by the target server matched the configured Authority ('{configuredAuthority}') - falling back to '{fallback}', which may not be the intended AS.");

        return fallback;
    }
}
