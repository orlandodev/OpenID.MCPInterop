using ModelContextProtocol.Authentication;

namespace OpenID.MCPInterop.Client.Partner;

/// <summary>
/// Minimal in-memory <see cref="ITokenCache"/> (the SDK's own InMemoryTokenCache
/// isn't public) that logs the access token's claims via TokenInspector the
/// moment it's received, for manual RFC 9068 verification during setup.
/// Never logs the raw token itself.
/// </summary>
internal sealed class LoggingTokenCache(PartnerSessionState session) : ITokenCache
{
    private TokenContainer? _tokens;

    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        TokenInspector.LogClaims(session, tokens.AccessToken);
        _tokens = tokens;
        return ValueTask.CompletedTask;
    }

    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(_tokens);
}
