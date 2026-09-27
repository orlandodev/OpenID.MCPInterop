using Duende.IdentityModel;
using OpenID.MCPInterop.Common.Constants;
using OpenID.MCPInterop.Common.Models;

namespace OpenID.MCPInterop.Client.Auth;

/// <summary>
/// Builds the <see cref="CimdMetadataDocument"/> this Client hosts at its
/// client_id URL, per draft-ietf-oauth-client-id-metadata-document. Kept
/// separate from Program.cs so the "client_id must byte-for-byte match the
/// hosted URL" invariant is unit-testable (see CimdDocumentFactoryTests).
/// A non-null <paramref name="jwksUri"/> makes it a confidential
/// private_key_jwt client per CIMD section 8.2; null keeps it public
/// (token_endpoint_auth_method "none").
/// </summary>
public static class CimdDocumentFactory
{
    public static CimdMetadataDocument Create(string cimdDocumentUrl, string redirectUri, string? jwksUri = null) => new()
    {
        ClientId = cimdDocumentUrl,
        ClientName = "OpenID.MCPInterop.Client",
        RedirectUris = [redirectUri],
        TokenEndpointAuthMethod = jwksUri is null
            ? OAuthConstants.NoneTokenEndpointAuthMethod
            : OidcConstants.EndpointAuthenticationMethods.PrivateKeyJwt,
        JwksUri = jwksUri,
    };
}
