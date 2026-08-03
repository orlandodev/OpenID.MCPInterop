using System.Text.Json.Serialization;

namespace OpenID.MCPInterop.Common.Models;

/// <summary>
/// Represents the JSON document hosted at a CIMD client_id URL, per
/// draft-ietf-oauth-client-id-metadata-document. The Client project should
/// serialize an instance of this and host it at a stable, HTTPS URL that
/// exactly matches the client_id it presents to the authorization server.
/// </summary>
public sealed class CimdMetadataDocument
{
    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    [JsonPropertyName("client_name")]
    public string? ClientName { get; init; }

    [JsonPropertyName("redirect_uris")]
    public required string[] RedirectUris { get; init; }

    [JsonPropertyName("token_endpoint_auth_method")]
    public string TokenEndpointAuthMethod { get; init; } = "none";

    [JsonPropertyName("grant_types")]
    public string[] GrantTypes { get; init; } = ["authorization_code"];

    [JsonPropertyName("response_types")]
    public string[] ResponseTypes { get; init; } = ["code"];

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}
