using System.ComponentModel.DataAnnotations;

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
