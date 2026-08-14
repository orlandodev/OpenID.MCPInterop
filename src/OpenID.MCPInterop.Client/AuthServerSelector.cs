namespace OpenID.MCPInterop.Client;

/// <summary>
/// Picks an authorization server out of a target server's advertised list, for
/// <see cref="ModelContextProtocol.Authentication.ClientOAuthOptions.AuthServerSelector"/>.
/// Prefers the AS matching <c>configuredAuthority</c> (trailing-slash/case-
/// insensitive) over the SDK's "first in the list" default. Always logs what
/// the PRM advertised, so "the client read the AS from the metadata" is
/// something the Log actually shows rather than just infers, and reports
/// when no match is found instead of silently connecting through the wrong
/// AS.
/// </summary>
internal static class AuthServerSelector
{
    public static Uri? Select(IReadOnlyList<Uri> servers, string configuredAuthority, Action<string> log)
    {
        log($"Protected resource metadata listed {servers.Count} authorization server(s): {string.Join(", ", servers)}");

        var match = servers.FirstOrDefault(server => string.Equals(
            server.ToString().TrimEnd('/'), configuredAuthority.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            log($"Selected '{match}' - matches the configured Authority.");
            return match;
        }

        var fallback = servers.FirstOrDefault();
        log(fallback is null
            ? $"No authorization server advertised by the target server matched the configured Authority ('{configuredAuthority}'), and none were advertised at all."
            : $"No authorization server advertised by the target server matched the configured Authority ('{configuredAuthority}') - falling back to '{fallback}', which may not be the intended AS.");

        return fallback;
    }
}
