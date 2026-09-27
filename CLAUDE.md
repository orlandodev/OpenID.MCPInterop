# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A .NET test harness for the OpenID Foundation's "MCP-Based AI Agent Security
with Open Identity Standards" interop event. It is a personal scaffold, not a
product. Both legs described below - Agent Governance (CIMD) and cross-org
(EMA/ID-JAG) - are implemented and confirmed working end-to-end against a
live Keycloak instance; check for any remaining `TODO` comments in a given
`Program.cs`/`Endpoints.cs` before assuming something isn't wired up, rather
than assuming the whole file is a stub.

Read [docs/architecture.md](docs/architecture.md) before making structural
changes - it maps each project to a step in the interop event's reference
architecture diagram and defines the build order (Agent Governance / CIMD
leg first, then the cross-org / EMA / ID-JAG leg). Two OAuth specs drive
almost everything here:

- **CIMD** (draft-ietf-oauth-client-id-metadata-document) - the MCP Client's
  `client_id` is a URL that resolves to a hosted `CimdMetadataDocument` JSON
  document, used for the Agent Governance leg (`Client` <-> `Server` via
  Keycloak).
- **ID-JAG / EMA** (draft-parecki-oauth-identity-assertion-authz-grant) - the
  cross-org leg. `Client` requests an ID-JAG from `Issuer` via RFC 8693 token
  exchange, then redeems it at a resource AS via the RFC 7523 JWT bearer
  grant to call a third-party MCP server.

## Commands

```bash
dotnet restore
dotnet build
dotnet test                                                          # all tests
dotnet test --filter "FullyQualifiedName~CimdMetadataDocumentTests"  # single class
dotnet test --filter "ClientId_ShouldMatch_HostedDocumentUrl"        # single test
dotnet run --project src/OpenID.MCPInterop.Server                                   # Keycloak scenario (default profile)
dotnet run --project src/OpenID.MCPInterop.Client --launch-profile Keycloak         # CIMD + EMA legs, this repo's own Keycloak
dotnet run --project src/OpenID.MCPInterop.Client --launch-profile 11AIBlockchain   # Leg 3, direct-trust, no CIMD
dotnet run --project src/OpenID.MCPInterop.Issuer
```

Targets `net10.0` (confirm your installed SDK with `dotnet --version`;
bump `TargetFramework` in the `.csproj` files if needed). Package versions
are centrally managed in [Directory.Packages.props](Directory.Packages.props)
via `ManagePackageVersionsCentrally` - add new packages there, not inline
version numbers in individual `.csproj` files. The MCP C# SDK ships fast, so
check for newer stable versions before building on top of the pinned one.

## Architecture

Four projects, one shared kernel:

- **`OpenID.MCPInterop.Common`** - shared models/constants referenced by
  every other project. `Models/CimdMetadataDocument.cs` and
  `Models/IdJagClaims.cs` are the canonical claim/field shapes - keep
  `Issuer` (minting) and its tests in sync with these rather than
  duplicating field lists. `Server` never deserializes into `IdJagClaims`
  itself - it only ever validates the ordinary OAuth access token Keycloak
  issues after redeeming an ID-JAG (RFC 7523), not the ID-JAG's own claims.
  `Constants/OAuthConstants.cs`
  centralizes every grant type / token type / well-known field string used
  across the flows; add new magic strings there, not inline.
- **`OpenID.MCPInterop.Server`** - the MCP server (`WithHttpTransport`,
  `WithToolsFromAssembly`), i.e. the "Agent Governance target" and,
  optionally, the "third-party MCP server" in the EMA flow. Rejects
  unauthenticated calls: `JwtBearer` validates the access token against
  whichever AS `Authorization:Authority`/`Audience`/`RequireHttpsMetadata`
  names, the SDK's `Mcp` scheme handles 401 challenges (RFC 9728
  protected-resource metadata), and `RequireAuthorization()` gates every MCP
  route - see `AuthorizationOptions.cs`/`Program.cs`. Like `Client`, it's
  config-driven across named scenarios (`Keycloak` - the default profile,
  matches `Client`'s `Keycloak` scenario; see `appsettings.{Scenario}.json`).
  Tools are static methods on `[McpServerToolType]` classes tagged
  `[McpServerTool]` (see `DemoTools.Ping()`).
- **`OpenID.MCPInterop.Client`** - MCP client test harness, browser-driven
  (a web UI with a Connect button and human-readable session log, not an
  auto-run console app) and config-driven across named scenarios
  (`ASPNETCORE_ENVIRONMENT`/`--launch-profile`: `Keycloak`,
  `11AIBlockchain` - see `appsettings.{Scenario}.json` and
  `docs/architecture.md`'s "Named scenarios"). `Client:UseCimd` toggles the
  primary leg between CIMD (hosts its own `CimdMetadataDocument`, drives a
  full authorization-code+PKCE flow via the MCP SDK's `ClientOAuthOptions`;
  by default as a `private_key_jwt` confidential client per CIMD section 8.2 -
  `Client:CimdAuthMethod`, `ClientSigningKey`, `PrivateKeyJwtHandler`)
  and direct-trust (pre-registered `ClientId`/`ClientSecret`, no CIMD
  document - Leg 3). `Client:UseEma` (only `true` for `Keycloak`) enables a
  second, independent leg: a dedicated login against Keycloak for a subject
  `id_token`, then the SDK's `IdentityAssertionGrantProvider` to request an
  ID-JAG from `Issuer` (RFC 8693) and redeem it at Keycloak (RFC 7523) - see
  `LoginFlows.cs`, `Endpoints.cs`, `Program.cs`.
- **`OpenID.MCPInterop.Issuer`** - stand-in enterprise IdP that mints
  ID-JAGs for local testing (no mainstream open-source IdP does this yet).
  `/token` is a complete RFC 8693 token-exchange endpoint (validates
  `subject_token` against Keycloak's real discovery/JWKS, checks `client_id`
  against a configured allowlist, mints a JWT with
  `OAuthConstants.IdJagHeaderTyp`/claims, returns per RFC 8693 - see
  `Endpoints.cs`). Signs with an **asymmetric** RSA key generated fresh
  in-memory each run, published at `/.well-known/jwks.json`, so a real
  resource AS can verify signatures without sharing a secret; no persistence
  across restarts (see `docs/keycloak-setup.md` for the Keycloak-side
  cache-reload consequence of that).

Two xUnit test projects, split by kind: `OpenID.MCPInterop.UnitTests`
(references `Common`/`Client`, exercises isolated classes directly - no
ASP.NET Core pipeline involved) and `OpenID.MCPInterop.IntegrationTests`
(references `Common`/`Issuer`, boots `Issuer`'s real `/token` and
`/.well-known/*` endpoints against an in-process `TestServer` - see
`Support/IssuerTestHost.cs` - with a fake IdP `HttpMessageHandler` standing
in for a live identity provider). Test names like
`ClientId_ShouldMatch_HostedDocumentUrl` intentionally encode known
interop footguns (e.g. `client_id` must byte-for-byte match the hosted CIMD
document URL) - when adding tests for new gotchas, prefer this
self-documenting style over generic names.

## Working in this repo

- External dependencies (Keycloak, with both `--features=cimd` and
  `--features=identity-assertion-jwt` enabled - see
  `deploy/keycloak/docker-compose.yml`) are not part of this repo and must
  be run separately - don't try to stand them up as part of a code change.
- If you do find a numbered `TODO` comment, follow its steps exactly - they
  were written against the specific RFC/draft section that applies (RFC
  8693, RFC 7523, or the ID-JAG draft) and the claim/field names must match
  `Common/Models` and `Common/Constants` precisely for interop with other
  participants' implementations.
