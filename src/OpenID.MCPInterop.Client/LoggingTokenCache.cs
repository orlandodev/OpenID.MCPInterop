using ModelContextProtocol.Authentication;

namespace OpenID.MCPInterop.Client;

/// <summary>
/// Minimal in-memory <see cref="ITokenCache"/> (the SDK's own InMemoryTokenCache
/// isn't public) that logs the access token's claims via TokenInspector the
/// moment it's received, for manual RFC 9068 verification during setup.
/// Never logs the raw token itself.
/// </summary>
internal sealed class LoggingTokenCache(ClientSessionState session) : ITokenCache
{
    private TokenContainer? _tokens;

    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        var claims = TokenInspector.LogClaims(session, tokens.AccessToken);
        if (claims is not null)
        {
            session.SetLastTokenClaims(claims);
        }

        _tokens = tokens;
        return ValueTask.CompletedTask;
    }

    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(_tokens);
}
