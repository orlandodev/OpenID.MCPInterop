using OpenID.MCPInterop.Client.Auth;
using OpenID.MCPInterop.Client.State;

namespace OpenID.MCPInterop.Client.Models;

/// <summary>Structured form of the claims <see cref="TokenInspector.LogClaims"/> decodes, for the UI's access-token panel (never the raw token itself). Public - exposed via <see cref="ClientSessionState.LastTokenClaims"/>.</summary>
public sealed record TokenClaims(string Typ, string Iss, string Sub, string Aud, string ClientId, DateTimeOffset? ExpiresAt);
