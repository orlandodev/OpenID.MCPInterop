# Local Keycloak setup (Agent Governance / CIMD leg, and EMA)

This sets up the Keycloak instance that `Server`'s `Authorization:Authority`
and `Client`'s `Ema:IdentityProviderAuthority`/`Ema:ResourceAuthority` config
(each project's `appsettings.json`) point at by default. The bulk of this doc
covers the CIMD-based Agent Governance
leg (`Client` <-> `Server`); see ["EMA leg (cross-org /
identity-assertion-jwt)"](#ema-leg-cross-org--identity-assertion-jwt) below
for the cross-org leg's Keycloak config - both are imported by the same
`setup.sh` run, see [`docs/architecture.md`](architecture.md) for how the
pieces fit together end to end.

CIMD support in Keycloak is an **experimental/preview feature** that has
moved between recent releases - if anything below doesn't match what you
see, check the current
[Keycloak MCP authorization server guide](https://www.keycloak.org/securing-apps/mcp-authz-server)
and [release notes](https://www.keycloak.org/docs/latest/release_notes/index.html)
before assuming this doc is stale.

## Quick start

```bash
./deploy/keycloak/setup.sh
```

Runs on Linux and macOS natively, and on Windows via
[Git Bash](https://git-scm.com/downloads) (already installed alongside Git
for most .NET developers - no PowerShell execution policy to fight with).
Pass `docker` instead of the default `podman` as the first argument
(`./deploy/keycloak/setup.sh docker`) if you're on Docker Desktop.

This trusts/exports the ASP.NET Core dev cert; on Windows, detects your
machine's LAN IP and writes it into a gitignored compose override (see
"Podman on Windows..." below for why - skipped on Linux/macOS, where it
shouldn't be needed); and starts Keycloak with the
realm/CIMD-policy/scope/test-user bundle in
[`deploy/keycloak/import/mcpinterop-realm.json`](../deploy/keycloak/import/mcpinterop-realm.json)
auto-imported - everything the old manual process did by hand (cert trust,
realm creation, the CIMD client policy, the audience-mapper scope),
confirmed against a real working setup and turned into one script.

**Verified vs. best-effort:** the Windows+Podman path (this script's
original reason for existing) has been run and confirmed working
end-to-end. The Linux/macOS paths are new and not yet verified on real
hardware - if something breaks there, the appendix below has the manual
steps to fall back to, and please report what went wrong so it can be
fixed for the next person.

Then:

```bash
dotnet run --project src/OpenID.MCPInterop.Server
dotnet run --project src/OpenID.MCPInterop.Client
```

Log in as `testuser` / `password` (created by the import) when the browser
opens.

**Previously known quirk (now actually fixed)**: `Client` used to open a
*second* browser tab almost immediately after the first, each with its own
login attempt (its own `state`/PKCE pair) - a real, ongoing annoyance, not
just a one-off. Two separate causes, found one at a time against a live
instance:

1. `HttpClientTransportOptions.TransportMode` defaults to `AutoDetect`,
   which probes both modern Streamable HTTP and legacy SSE as separate
   connection attempts - each one independently hit Server's 401 and
   independently triggered OAuth. `Client` pins `TransportMode` to
   `StreamableHttp` (the only mode `Server` actually speaks) to remove this.
2. That alone didn't fully fix it - `EnableStandaloneGetStream` defaults to
   `true`, which opens a *second*, separate GET connection (for
   server-initiated push) alongside the POST used for the initialize
   handshake, and that GET independently hit the 401 and triggered its own
   OAuth flow too. `Server` has nothing server-initiated to push today
   (`DemoTools.Ping` is plain request/response), so `Client` now sets
   `EnableStandaloneGetStream = false` as well.

With both set, exactly one browser prompt appears per leg - confirmed
against a live instance, not just inferred from the SDK's defaults.

If that all works, you're done - skip to
["If mcp:tools didn't auto-attach"](#if-mcptools-didnt-auto-attach-to-the-cimd-client)
only if `Client` errors after login, and the rest of this doc only if
something above didn't work as described.

## What's imported automatically

`mcpinterop-realm.json` bundles four things, straight from a manually
verified working configuration (not guessed at):

- The `mcpinterop` realm itself.
- A **client policy**: a *profile* (`cimd-profile`) adding the
  `client-id-metadata-document` executor - trusting `client_id`/`redirect_uri`
  hosts `client.dev.internal` and `127.0.0.1`, HTTPS-only, same-domain
  restriction off (`client_id` and `redirect_uri` deliberately use
  different hosts - see the Podman section below for why) - plus a *policy*
  (`cimd-policy`) requiring HTTPS `client_id` URLs on `client.dev.internal`
  and applying that profile. Without this, `--features=cimd` alone doesn't
  make Keycloak treat a URL as a valid `client_id` - you'd see
  "Client not found".
- The `mcp:tools` **client scope**, with an `oidc-audience-mapper`
  protocol mapper setting `aud` to `http://localhost:5000` (`Server`'s
  `Authorization:Audience`). Keycloak doesn't support RFC 8707 resource
  indicators yet, so this scope-based mapper is the workaround for getting
  a correct `aud` claim at all.
- A **test user** (`testuser`/`password`) to log in as - `admin`/`admin`
  only exists in the `master` realm and can't log into an application flow.

If you ever need to redo any of this by hand (different realm name, a
container engine where the script doesn't apply, etc.), every value above
came from a real `kcadm.sh get realms/mcpinterop` /
`kcadm.sh get client-scopes -r mcpinterop` export - copy the shape from
`mcpinterop-realm.json` directly rather than re-deriving it from the admin
console.

## If `mcp:tools` didn't auto-attach to the CIMD client

The import sets `mcp:tools` as a realm-wide `defaultOptionalClientScopes`
entry, as an attempt to have it auto-assigned to the CIMD client Keycloak
materializes the first time it resolves that `client_id` - avoiding a
manual per-client step. **This is unverified for CIMD's dynamically
materialized clients specifically** (it's standard, well-documented
behavior for normally-registered clients). If `Client` gets past login but
then fails calling `Server` (Server rejects the token, or `Client` throws
about a missing/wrong `aud`), do this one manual step:

1. Log in via `Client` once, so Keycloak actually materializes the CIMD
   client entry.
2. Admin console -> **Clients** -> find
   `https://client.dev.internal:5050/client-metadata.json`.
3. Its **Client scopes** tab -> **Add client scope** -> select `mcp:tools`
   -> **Add**.

If you hit this, the auto-attach didn't work as hoped - no need to redo
anything else, this is additive on top of what already imported correctly.

## Podman on Windows can't reach the host via `host.docker.internal`

On Docker Desktop, `host.docker.internal` "just works" for a container to
reach an arbitrary port on the Windows host. **On Podman with `podman
machine` on Windows, it doesn't reliably work for that** - confirmed on
this project: `host.docker.internal` resolved fine and reached *something*
(Keycloak's logs showed `Connect to host.docker.internal:5050
[host.docker.internal/169.254.1.2] failed: Connection refused`), and
opening a Windows Firewall rule for the port made no difference. That
combination points at Podman's gvproxy network proxy itself not forwarding
arbitrary host ports, not at DNS or the firewall - a known rough edge with
Podman's Windows networking, not something wrong with your setup.

**Workaround** (what `setup.sh` automates on Windows): skip host-alias
resolution and point straight at your machine's real LAN IP instead, but
keep using a hostname (not the bare IP) so it still matches the dev cert's
`*.dev.internal` SAN. `docker-compose.override.yml` does this via
`extra_hosts: - "client.dev.internal:<your LAN IP>"`, injected straight
into the Keycloak container's `/etc/hosts` - no dependency on gvproxy's
port-forwarding at all. `Client:CimdDocumentUrl` already points at
`client.dev.internal` (see its `appsettings.json`) to match. If your LAN IP
changes (DHCP), re-run `setup.sh` to regenerate the override. If you're on
native Linux Podman (no VM) and hit this same error anyway, re-run with
`FORCE_LAN_WORKAROUND=1 ./deploy/keycloak/setup.sh` to try the same fix
there.

If you're on Docker Desktop, you likely don't need any of this - delete
`docker-compose.override.yml` and change `Client:CimdDocumentUrl` back to
`host.docker.internal`, and drop `client.dev.internal`/`127.0.0.1` from the
realm import's `cimd-allow-permitted-domains` in favor of
`host.docker.internal`.

## Troubleshooting: diagnosing "Client Metadata fetch failed" directly

If you still see this after the quick start, diagnose directly rather than
guessing - Keycloak's own logs show the real underlying exception (no
extra tooling needed inside the container, unlike `curl` which recent
Keycloak images don't ship):

```bash
podman logs mcpinterop-keycloak --tail 200
```

- `UnknownHostException` for `client.dev.internal` -> the `extra_hosts`
  entry isn't in effect - confirm `docker-compose.override.yml` exists
  (`setup.sh` should have created it, on Windows at least) and the
  container was recreated after it was written (`setup.sh` already does
  `--force-recreate`).
- `Connection refused`/timeout to the LAN IP itself -> `Client` isn't
  actually running right now, or the LAN IP is stale (DHCP) - re-run
  `setup.sh`.
- `SSLHandshakeException` / `PKIX path building failed` -> Keycloak's Java
  truststore doesn't trust the ASP.NET Core dev cert - confirm
  `deploy/keycloak/certs/aspnetcore-dev-cert.pem` exists and the container
  was recreated after it was exported.

The authorization-code redirect (the browser landing back on `Client`'s
`/callback`) hits none of this - that's a browser-to-host hop over a cert
your OS already trusts, from a browser that isn't inside any container, so
Podman's container-networking limitations don't apply to it.

## EMA leg (cross-org / identity-assertion-jwt)

Keycloak's own docs call `identity-assertion-jwt` "experimental... may
introduce breaking changes... do not use in production" - more explicitly
preview than CIMD. What's below started from the one official documented
example for this feature (Keycloak's
[JWT Authorization Grant](https://www.keycloak.org/securing-apps/jwt-authorization-grant)
and
[Identity Assertion JWT Authorization Grant](https://www.keycloak.org/securing-apps/identity-assertion-jwt-authorization-grant)
guides, fetched directly rather than assumed) and has since been **confirmed
working end-to-end against a live Keycloak instance** - both `Ping result`
lines print successfully in one `Client` run. Getting there took five rounds
of real-instance iteration on top of the doc research, same shape as the
CIMD leg's client policy needing empirical correction - the concrete fixes
are below in case you hit the same errors.

### What's imported for the EMA leg

`mcpinterop-realm.json` also bundles (same `setup.sh` run as the CIMD leg,
no separate step):

- **`mcpinterop-ema-login`**: a public PKCE client `Client` uses for a
  second, dedicated login (separate from the CIMD leg's) purely to get a
  subject `id_token` - see `Ema:LoginClientId`/`LoginRedirectUri` in
  `Client`'s `appsettings.json`.
- **`mcpinterop-ema-resource`**: a confidential client with
  `oauth2.jwt.authorization.grant.enabled` / `.grant.idp` attributes, per
  Keycloak's JWT Authorization Grant guide - only confidential clients can
  request this grant. Its secret is a local-only placeholder
  (`local-dev-ema-resource-secret-change-me`, matching `Client`'s
  `Ema:ResourceClientSecret`) - override via `dotnet user-secrets` in real
  use, never commit a real one. This is also the `client_id` `Issuer` embeds
  in the ID-JAG's `client_id` claim (see below) - Keycloak requires the two
  to match.
- An **identity provider** (`alias: mcpinterop-issuer`) trusting `Issuer` as
  the ID-JAG source: `issuer`/`jwksUrl` point at `Issuer`'s endpoints via
  `client.dev.internal:5100` (not `localhost:5100` - see "Container
  networking" below), plus the `jwtAuthorizationGrantEnabled` family of
  config keys from Keycloak's `kcadm` example. `authorizationUrl`/
  `config.clientId`/`config.clientSecret` are required by the generic `oidc`
  provider type's config schema but never actually exercised by this flow -
  `Issuer` has no interactive login endpoint of its own (see
  `docs/architecture.md`'s "EMA leg wiring": Keycloak, not `Issuer`, plays
  the interactive-login role here).
- A **`federatedIdentities`** entry linking `testuser` to that identity
  provider, under `testuser`'s own (now explicitly pinned) Keycloak user ID.
  This is the non-obvious part: the ID-JAG's `sub` claim is copied straight
  from the Keycloak-issued `id_token` `Issuer` validated it against, i.e.
  it's `testuser`'s own Keycloak-internal ID - so for Keycloak to accept the
  JWT-bearer redemption, it has to resolve that same ID back to `testuser`
  via a federated-identity link. Without `testuser`'s `id` being pinned in
  the import (rather than left to auto-generate), that link couldn't be
  wired up ahead of time.

### Real gotchas hit getting this working (fixed in code, worth knowing)

- **Container networking, again**: Keycloak fetches `Issuer`'s JWKS
  server-to-server, from inside its container - `localhost:5100` there means
  the container itself, same class of problem the CIMD leg's
  `client.dev.internal` workaround already exists for. `Issuer:Url` (what
  gets embedded as the ID-JAG's `iss` claim and registered as Keycloak's
  `issuer` config) now points at `client.dev.internal:5100`, while `Issuer`
  itself binds to `0.0.0.0` (`Issuer:ListenUrl`) so it actually accepts
  connections arriving via the LAN IP - same `ListenUrl`-vs-public-URL split
  `Client` already uses for its CIMD document. `Client`'s own direct call to
  `/token` stays on plain `localhost:5100`, since `Client` runs on the same
  host as `Issuer`, not inside a container.
- **`http://` discovery rejected by default**: `Microsoft.IdentityModel`'s
  `HttpDocumentRetriever` refuses non-HTTPS addresses
  (`IDX20108`) unless told otherwise. `Issuer` now constructs one explicitly
  with `RequireHttps` set from `Issuer:RequireHttpsMetadata`, so local dev's
  plain-HTTP `Issuer:IdentityProviderAuthority` works without weakening
  anything if you point this at a real HTTPS authority later.
- **ID-JAG `client_id` claim must match the redeeming client**: Keycloak
  rejected the JWT-bearer grant with `invalid_grant ("client id in
  assertion" vs "client id in request")` until the client_id `Client` sends
  `Issuer` for the RFC 8693 exchange (step 3) was changed to the *same*
  `mcpinterop-ema-resource` client_id used to redeem the assertion at
  Keycloak (step 4) - a single consistent client identity across both steps,
  not two different ones.
- **`resource` and `audience` are not interchangeable**: RFC 8693's `aud`
  claim needs to be the resource AS being targeted (Keycloak's own issuer),
  not the target MCP server - Keycloak rejected the grant with
  `invalid_grant (Invalid token audience)` while `Issuer` echoed the
  `resource` form field (the MCP server URL) into `aud` instead of the
  `audience` field (Keycloak's issuer). `Issuer`'s `/token` now reads both
  separately.
- **Stale cached JWKS after restarting `Issuer`**: `Issuer` generates a
  fresh RSA key pair every run (no persistence), but Keycloak caches the
  identity provider's fetched JWKS and won't necessarily refetch just
  because `Issuer` restarted - manifests as `invalid_grant (Invalid
  signature)` even though everything else is correct. Force a refetch after
  restarting `Issuer`:
  ```bash
  TOKEN=$(curl -s -X POST http://localhost:8080/realms/master/protocol/openid-connect/token \
    -d grant_type=password -d client_id=admin-cli -d username=admin -d password=admin \
    | python3 -c "import json,sys; print(json.load(sys.stdin)['access_token'])")
  curl -s "http://localhost:8080/admin/realms/mcpinterop/identity-provider/instances/mcpinterop-issuer/reload-keys" \
    -H "Authorization: Bearer $TOKEN"
  ```

### Verifying

```bash
curl http://localhost:5100/.well-known/jwks.json
```

should return one RSA key (changes every time `Issuer` restarts - see the
stale-cache gotcha above if you restart it after Keycloak has already
fetched the old one). Then run `Server`, then `Client`, same as the CIMD
"Quick start" above - expect **two** browser logins (the CIMD login, then
the EMA leg's dedicated login), and two distinct `Ping result` lines in
`Client`'s console, one labeled per leg:

```
Ping result (Agent Governance / CIMD leg): pong from OpenID.MCPInterop.Server
Ping result (EMA / cross-org leg): pong from OpenID.MCPInterop.Server
```

If the JWT-bearer redemption fails at Keycloak on a fresh setup (not a
restart), check first whether a *new* client/identity-provider inside an
*already-existing* realm actually got created: `--import-realm` skips
realms that already exist, so if `mcpinterop-keycloak`'s data volume
predates this EMA config being added, recreating the container alone won't
pick it up - remove the `keycloak-data` volume (or rename the realm) and
re-run `setup.sh` for a clean import, the same data-loss-shaped lesson the
CIMD leg's setup already called out once.

## Appendix: doing it by hand

Only needed if `setup.sh` doesn't apply to your setup (no bash available,
a container engine where the script doesn't quite fit, or you want to
understand exactly what got automated).

**Trust the dev cert for Keycloak specifically** (Windows trusting it for
your browser and Keycloak's Java truststore are separate trust stores):

```bash
dotnet dev-certs https --trust
mkdir -p deploy/keycloak/certs
dotnet dev-certs https -ep deploy/keycloak/certs/aspnetcore-dev-cert.pem --format Pem --no-password
rm deploy/keycloak/certs/aspnetcore-dev-cert.key
```

**Start Keycloak** (the realm import happens automatically via
`--import-realm` in the compose file regardless of whether you use
`setup.sh`):

```bash
podman compose -f deploy/keycloak/docker-compose.yml up -d
```

**Recreate** (e.g. after editing `docker-compose.override.yml` by hand) if
it was already running:

```bash
podman compose -f deploy/keycloak/docker-compose.yml up -d --force-recreate
```

If you want to reproduce the client policy / client scope by hand instead
of relying on the import (e.g. a different realm name), the admin-console
click path is: **Realm Settings -> Client Policies -> Profiles tab ->
Create client profile**, add the `client-id-metadata-document` executor;
**Policies tab -> Create client policy**, add a `client-id-uri` condition,
attach the profile. Match the exact field values in
`deploy/keycloak/import/mcpinterop-realm.json`'s `clientProfiles` /
`clientPolicies` rather than guessing - the UI field labels
("Trusted domains", "Allow http scheme", etc.) map to that JSON's
`cimd-allow-permitted-domains`, `cimd-allow-http-scheme`, and so on, which
was tedious to reverse-engineer the first time around.

Everything here is one-line-per-command on purpose (no `\` continuations) -
those are bash syntax and silently break when pasted into PowerShell or
cmd. Every `kcadm.sh -s key.with.dots="value"` style command has the same
problem in PowerShell (embedded quotes get mangled forwarding to a native
exe) - use `deploy/keycloak/import/mcpinterop-realm.json` as a full-file
import instead of trying to reconstruct individual `kcadm create` commands
by hand.
