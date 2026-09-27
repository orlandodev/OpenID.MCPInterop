using System.Text.Json.Serialization;
using OpenID.MCPInterop.Common.Constants;

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
    public string TokenEndpointAuthMethod { get; init; } = OAuthConstants.NoneTokenEndpointAuthMethod;

    [JsonPropertyName("grant_types")]
    public string[] GrantTypes { get; init; } = ["authorization_code"];

    [JsonPropertyName("response_types")]
    public string[] ResponseTypes { get; init; } = ["code"];

    /// <summary>
    /// Space-delimited per RFC 7591 section 2. Omitted from the serialized
    /// document when null rather than emitted as <c>"scope": null</c>, which
    /// isn't a valid value and a strict AS may reject the whole document
    /// because of it.
    /// </summary>
    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }

    /// <summary>
    /// Where the AS fetches this client's public signing keys when
    /// <see cref="TokenEndpointAuthMethod"/> is <c>private_key_jwt</c> (CIMD
    /// section 8.2). Public keys only - section 4.1 forbids private key
    /// material or a client_secret anywhere in this document. Omitted from
    /// the serialized document when null (public clients).
    /// </summary>
    [JsonPropertyName("jwks_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JwksUri { get; init; }
}
