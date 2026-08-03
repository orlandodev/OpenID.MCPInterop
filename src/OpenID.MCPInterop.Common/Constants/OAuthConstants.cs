namespace OpenID.MCPInterop.Common.Constants;

/// <summary>
/// Grant types, token types, and media types used across the interop test
/// architecture (CIMD, RFC 8693 token exchange, and the ID-JAG / EMA flow).
/// Centralizing these avoids magic strings scattered across Client/Server/Issuer.
/// </summary>
public static class OAuthConstants
{
    // --- RFC 8693 Token Exchange (client -> enterprise IdP, requesting an ID-JAG) ---
    public const string TokenExchangeGrantType = "urn:ietf:params:oauth:grant-type:token-exchange";

    // --- draft-parecki-oauth-identity-assertion-authz-grant (ID-JAG / EMA) ---
    public const string IdJagGrantProfile = "urn:ietf:params:oauth:grant-profile:id-jag";
    public const string IdJagTokenType = "urn:ietf:params:oauth:token-type:id-jag";
    public const string IdJagHeaderTyp = "oauth-id-jag+jwt";

    // --- RFC 7523 JWT Bearer Grant (client -> third-party/resource AS, redeeming the ID-JAG) ---
    public const string JwtBearerGrantType = "urn:ietf:params:oauth:grant-type:jwt-bearer";

    // --- AS metadata field indicating ID-JAG support (used for discovery) ---
    public const string AuthorizationGrantProfilesSupportedField = "authorization_grant_profiles_supported";

    // --- CIMD (draft-ietf-oauth-client-id-metadata-document) ---
    public const string CimdMetadataSupportedField = "client_id_metadata_document_supported";

    // --- ID-JAG claim names (minted by Issuer, see Common/Models/IdJagClaims.cs) ---
    public const string ClientIdClaim = "client_id";
    public const string ResourceClaim = "resource";
    public const string ScopeClaim = "scope";
}
