using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using OpenID.MCPInterop.Common.Constants;
using OpenID.MCPInterop.Common.Models;

namespace OpenID.MCPInterop.Issuer;

public static class Endpoints
{
    /// <summary>
    /// Maps Issuer's RFC 8693 token-exchange endpoint plus the discovery/JWKS
    /// endpoints a resource AS needs to validate the ID-JAGs it mints.
    /// Generates Issuer's RSA signing key here too - once per process
    /// lifetime, in-memory only (see docs/architecture.md's "known rough
    /// edges": ephemeral/regenerated per run is fine for local testing).
    /// </summary>
    public static WebApplication MapIssuerEndpoints(this WebApplication app, IssuerOptions options)
    {
        var rsaKey = RSA.Create(2048);
        var keyId = Guid.NewGuid().ToString("N");
        var signingCredentials = new SigningCredentials(new RsaSecurityKey(rsaKey) { KeyId = keyId }, SecurityAlgorithms.RsaSha256);

        var publicRsaKey = RSA.Create();
        publicRsaKey.ImportParameters(rsaKey.ExportParameters(includePrivateParameters: false));
        var publicJwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(publicRsaKey) { KeyId = keyId });
        publicJwk.Use = "sig";
        publicJwk.Alg = SecurityAlgorithms.RsaSha256;

        // Validates subject tokens presented to /token against the configured
        // OpenID Provider, fetching its real signing keys from its discovery
        // document - the same mechanism
        // Microsoft.AspNetCore.Authentication.JwtBearer uses internally,
        // applied by hand here since /token reads a raw form field, not the
        // request's own Authorization header. RequireHttps: HttpDocumentRetriever
        // refuses non-HTTPS addresses by default (confirmed against a live
        // instance: IDX20108) - governed by the explicit RequireHttpsMetadata
        // setting (default true; only ever false for a local-dev, plain-http
        // OpenID Provider), not inferred from IdentityProviderAuthority's
        // literal scheme.
        var subjectTokenDocumentRetriever = new HttpDocumentRetriever(app.Services.GetRequiredService<IHttpClientFactory>().CreateClient())
        {
            RequireHttps = options.RequireHttpsMetadata,
        };
        var subjectTokenConfigManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{options.IdentityProviderAuthority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            subjectTokenDocumentRetriever);

        // RFC 8693 Token Exchange endpoint: the client presents its ID Token
        // here and asks for an ID-JAG scoped to a specific target resource.
        app.MapPost("/token", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.Json(
                    new { error = "invalid_request", error_description = "Expected an application/x-www-form-urlencoded body." },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // 1. Read grant_type, subject_token, and the target resource/audience
            //    from the form-encoded request body.
            var form = await request.ReadFormAsync(cancellationToken);

            var grantType = form["grant_type"].ToString();
            if (grantType != OAuthConstants.TokenExchangeGrantType)
            {
                return Results.Json(new { error = "unsupported_grant_type" }, statusCode: StatusCodes.Status400BadRequest);
            }

            var subjectToken = form["subject_token"].ToString();
            var clientId = form["client_id"].ToString();
            // resource is the target MCP server this Client ultimately wants
            // to call - informational, carried into the ID-JAG's resource
            // claim. The aud claim is NOT taken from the caller at all (see
            // below): a caller-supplied audience/resource value used to be
            // trusted for aud too, which let any RFC-8693-compliant caller
            // that omitted 'audience' (spec-legal - RFC 8693 doesn't require
            // it) put the *target MCP server's* URL in aud instead of
            // Keycloak's, reproducing the exact "Invalid token audience"
            // failure this endpoint was already fixed once against.
            var resource = form["resource"].ToString();

            if (string.IsNullOrEmpty(subjectToken) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(resource))
            {
                return Results.Json(
                    new { error = "invalid_request", error_description = "subject_token, client_id, and resource are required." },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // 1b. Authorization policy, part one: client_id is only trusted
            //     if it's on Issuer's own configured allowlist - the subject
            //     check below establishes *who* is asking, not *which client*
            //     they're allowed to claim to be minting this ID-JAG for.
            if (!options.TrustedClientIds.Contains(clientId, StringComparer.Ordinal))
            {
                return Results.Json(
                    new { error = "invalid_client", error_description = "client_id is not trusted by this Issuer." },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // 2. Validate the subject_token against the IdP session it came from -
            //    signature, issuer, and expiry checked against the configured
            //    OpenID Provider's own published keys, fetched (and cached) via
            //    discovery.
            ClaimsPrincipal subjectPrincipal;
            try
            {
                var identityProviderConfig = await subjectTokenConfigManager.GetConfigurationAsync(cancellationToken);
                var handler = new JwtSecurityTokenHandler();
                subjectPrincipal = handler.ValidateToken(subjectToken, new TokenValidationParameters
                {
                    ValidIssuer = identityProviderConfig.Issuer,
                    IssuerSigningKeys = identityProviderConfig.SigningKeys,
                    // The EMA login client's own audience isn't known to Issuer in
                    // advance - it's whichever public client Client authenticated
                    // with at the OpenID Provider, not a fixed value Issuer can pin here.
                    ValidateAudience = false,
                    ValidateLifetime = true,
                }, out _);
            }
            // SecurityTokenException covers a well-formed JWT that fails
            // signature/issuer/expiry checks; a subject_token that isn't a
            // parseable JWT at all throws SecurityTokenMalformedException
            // instead, which derives from SecurityTokenArgumentException (an
            // ArgumentException), not SecurityTokenException - confirmed by a
            // live throwaway repro, not documented anywhere obvious. Both are
            // "the caller sent a bad assertion", so both map to invalid_grant.
            catch (Exception ex) when (ex is SecurityTokenException or SecurityTokenArgumentException)
            {
                return Results.Json(
                    new { error = "invalid_grant", error_description = ex.Message },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var subject = subjectPrincipal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? subjectPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? throw new InvalidOperationException("Validated subject token did not carry a 'sub' claim.");

            // 3. Authorization policy, part two: beyond the client_id allowlist
            //    above, the subject check is deliberately trivial - any subject
            //    holding a validated Keycloak ID token is allowed through. A real
            //    enterprise IdP's policy engine would add per-subject/per-resource
            //    rules here; kept trivial as a local-scaffold simplification, not
            //    a dangling TODO.
            var requestedScopes = form["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var now = DateTimeOffset.UtcNow;

            // 4. Mint an ID-JAG using the shared claim shape. Audience is
            //    Issuer's own configured resource AS (ResourceAuthority), never
            //    caller input - see the comment on `resource` above.
            var idJagClaims = new IdJagClaims
            {
                Issuer = options.Url,
                Subject = subject,
                Audience = options.ResourceAuthority,
                ClientId = clientId,
                Resource = resource,
                Scope = requestedScopes.Length > 0 ? requestedScopes : null,
                JwtId = Guid.NewGuid().ToString("N"),
                IssuedAt = now,
                ExpiresAt = now.AddMinutes(2),
            };
            var idJag = IdJagTokenFactory.CreateJwt(idJagClaims, signingCredentials);

            // 5. Return per RFC 8693.
            return Results.Json(new
            {
                access_token = idJag,
                issued_token_type = OAuthConstants.IdJagTokenType,
                token_type = "N_A",
            });
        });

        // Publishes the RSA public key so a resource AS (Keycloak's
        // identity-assertion-jwt feature) can verify this Issuer's ID-JAG
        // signatures without sharing a secret.
        app.MapGet("/.well-known/jwks.json", () => Results.Json(new { keys = new[] { publicJwk } }));

        // Minimal discovery so a resource AS (or your own client) can find this
        // endpoint programmatically, mirroring what a real IdP would publish.
        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = options.Url,
            token_endpoint = $"{options.Url}/token",
            jwks_uri = $"{options.Url}/.well-known/jwks.json",
            grant_types_supported = new[] { OAuthConstants.TokenExchangeGrantType },
        }));

        return app;
    }
}
