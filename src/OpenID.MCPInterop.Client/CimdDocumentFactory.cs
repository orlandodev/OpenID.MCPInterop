using OpenID.MCPInterop.Common.Models;

namespace OpenID.MCPInterop.Client;

/// <summary>
/// Builds the <see cref="CimdMetadataDocument"/> this Client hosts at its
/// client_id URL, per draft-ietf-oauth-client-id-metadata-document. Kept
/// separate from Program.cs so the "client_id must byte-for-byte match the
/// hosted URL" invariant is unit-testable (see CimdDocumentFactoryTests).
/// </summary>
public static class CimdDocumentFactory
{
    public static CimdMetadataDocument Create(string cimdDocumentUrl, string redirectUri) => new()
    {
        ClientId = cimdDocumentUrl,
        ClientName = "OpenID.MCPInterop.Client",
        RedirectUris = [redirectUri],
    };
}
