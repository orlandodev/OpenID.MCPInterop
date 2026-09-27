using System.ComponentModel.DataAnnotations;
using Duende.IdentityModel;
using OpenID.MCPInterop.Common.Constants;

namespace OpenID.MCPInterop.Client.Options;

/// <summary>
/// Config for whichever leg this scenario's <see cref="UseCimd"/>/<see cref="UseEma"/>
/// toggles select - CIMD (hosted <see cref="CimdDocumentUrl"/>), direct-trust
/// (pre-registered <see cref="ClientId"/>/<see cref="ClientSecret"/> at
/// <see cref="Authority"/>), and optionally EMA on top of either. See
/// docs/architecture.md for how the named scenarios (Keycloak,
/// 11AIBlockchain) map onto these toggles.
/// </summary>
public sealed class ClientOptions : IValidatableObject
{
    public const string SectionName = "Client";

    public bool UseCimd { get; init; }

    public bool UseEma { get; init; }

    /// <summary>Required when <see cref="UseCimd"/> is true - the hosted CIMD document's own URL (must byte-for-byte match, see CimdDocumentFactory).</summary>
    public string? CimdDocumentUrl { get; init; }

    /// <summary>
    /// Only meaningful when <see cref="UseCimd"/> is true - the
    /// token_endpoint_auth_method the hosted CIMD document advertises.
    /// <c>private_key_jwt</c> (the default) makes this a confidential client
    /// per CIMD section 8.2: the document gains a <see cref="JwksUri"/>, and
    /// every token request carries an RFC 7523 section 2.2 client_assertion
    /// (see PrivateKeyJwtHandler). <c>none</c> keeps it a public, PKCE-only
    /// client, for an AS that doesn't accept confidential CIMD clients.
    /// </summary>
    public string CimdAuthMethod { get; init; } = OidcConstants.EndpointAuthenticationMethods.PrivateKeyJwt;

    /// <summary>PEM file holding the private_key_jwt signing key, relative to the content root. Generated on first run, reused afterwards so its kid stays stable (see ClientSigningKey).</summary>
    public string CimdSigningKeyPath { get; init; } = "keys/cimd-signing.pem";

    public bool UsePrivateKeyJwt =>
        UseCimd && CimdAuthMethod == OidcConstants.EndpointAuthenticationMethods.PrivateKeyJwt;

    /// <summary>
    /// The public JWKS URL, resolved relative to <see cref="CimdDocumentUrl"/>
    /// so it always shares the CIMD document's origin (and any tunnel host)
    /// without a separate setting to keep in sync. Null unless
    /// <see cref="UsePrivateKeyJwt"/>.
    /// </summary>
    public string? JwksUri => UsePrivateKeyJwt && CimdDocumentUrl is not null
        ? new Uri(new Uri(CimdDocumentUrl), "jwks.json").ToString()
        : null;

    /// <summary>Required when <see cref="UseCimd"/> is false - a pre-registered client at <see cref="Authority"/>.</summary>
    public string? ClientId { get; init; }

    /// <summary>Null for a public client (e.g. PKCE-only); set for a confidential client registration. Only meaningful when <see cref="UseCimd"/> is false.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// Required when <see cref="UseCimd"/> is false - the hosted AS's issuer
    /// URL, used to disambiguate the target server's advertised AS list if it
    /// has more than one (see AuthServerSelector). Optional even when
    /// <see cref="UseCimd"/> is true, as an extra safety net for CIMD
    /// scenarios whose server advertises more than one AS.
    /// </summary>
    public string? Authority { get; init; }

    [Required]
    public string RedirectUri { get; init; } = string.Empty;

    /// <summary>What Kestrel binds to.</summary>
    public string ListenUrl { get; init; } = "https://0.0.0.0:5050";

    public string[] Scopes { get; init; } = ["openid", "mcp:tools"];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (UseCimd && string.IsNullOrWhiteSpace(CimdDocumentUrl))
        {
            yield return new ValidationResult(
                $"{nameof(CimdDocumentUrl)} is required when {nameof(UseCimd)} is true.",
                [nameof(CimdDocumentUrl)]);
        }

        if (UseCimd
            && CimdAuthMethod is not (OidcConstants.EndpointAuthenticationMethods.PrivateKeyJwt or OAuthConstants.NoneTokenEndpointAuthMethod))
        {
            yield return new ValidationResult(
                $"{nameof(CimdAuthMethod)} must be '{OidcConstants.EndpointAuthenticationMethods.PrivateKeyJwt}' or '{OAuthConstants.NoneTokenEndpointAuthMethod}'.",
                [nameof(CimdAuthMethod)]);
        }

        if (!UseCimd && string.IsNullOrWhiteSpace(ClientId))
        {
            yield return new ValidationResult(
                $"{nameof(ClientId)} is required when {nameof(UseCimd)} is false.",
                [nameof(ClientId)]);
        }

        if (!UseCimd && string.IsNullOrWhiteSpace(Authority))
        {
            yield return new ValidationResult(
                $"{nameof(Authority)} is required when {nameof(UseCimd)} is false.",
                [nameof(Authority)]);
        }
    }
}
