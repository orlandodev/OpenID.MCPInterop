#!/usr/bin/env bash
# One-shot local Keycloak setup for the Agent Governance (CIMD) leg.
#
# Automates everything that can safely be automated per docs/keycloak-setup.md:
#   1. Trusts/exports the ASP.NET Core dev cert so Keycloak's Java truststore
#      accepts the HTTPS-hosted CIMD document Client serves.
#   2. On Windows only: detects this machine's real LAN IP and writes it into
#      a gitignored docker-compose.override.yml, working around Podman-on-Windows
#      not reliably forwarding host.docker.internal to arbitrary host ports.
#      Skipped on Linux/Mac, where this specific workaround shouldn't be needed
#      (see docs/keycloak-setup.md) - set FORCE_LAN_WORKAROUND=1 if you hit
#      "Client Metadata fetch failed" anyway and want to try it there too.
#   3. Starts Keycloak via the container engine, auto-importing the realm,
#      CIMD client policy, mcp:tools scope+audience-mapper, and a test user
#      from deploy/keycloak/import/mcpinterop-realm.json.
#
# What this does NOT do (and why):
#   - Doesn't touch your firewall. That's a system/security setting - if you
#     hit connection-refused errors, the fix is yours to apply (see
#     docs/keycloak-setup.md's troubleshooting section for the Windows case).
#   - Doesn't guarantee mcp:tools ends up assigned to the CIMD client. The
#     realm import tries this via defaultOptionalClientScopes, but that's
#     unverified for CIMD's dynamically-materialized clients - see
#     docs/keycloak-setup.md's troubleshooting appendix if login succeeds but
#     Server rejects the token afterward.
#
# Usage: ./deploy/keycloak/setup.sh [podman|docker]
#
# Runs on Linux and macOS natively, and on Windows via Git Bash (already
# installed alongside Git for most .NET developers) - no PowerShell execution
# policy to fight with. The Windows LAN-IP detection path (step 2) shells out
# to powershell.exe for that one lookup, reusing logic already confirmed
# working in this project's original Windows-only script; everything else in
# this file is plain POSIX shell.
#
# Honesty check: the Windows+Podman path (this whole script's reason for
# existing) has been run and confirmed working end-to-end. The Linux/macOS
# paths are new and NOT yet verified on real hardware - if something here is
# wrong for your platform, docs/keycloak-setup.md's appendix has the manual
# steps to fall back to, and please report what broke.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONTAINER_ENGINE="${1:-podman}"
COMPOSE_FILE="$SCRIPT_DIR/docker-compose.yml"
OVERRIDE_PATH="$SCRIPT_DIR/docker-compose.override.yml"
CERT_DIR="$SCRIPT_DIR/certs"
CERT_PATH="$CERT_DIR/aspnetcore-dev-cert.pem"
KEY_PATH="$CERT_DIR/aspnetcore-dev-cert.key"

step() { echo; echo "==> $1"; }

# --- 1. Dev cert: trust + export for Keycloak's truststore ---
step "Trusting ASP.NET Core dev cert"
if ! dotnet dev-certs https --trust; then
  echo "Note: 'dotnet dev-certs https --trust' can't fully trust a cert on Linux (no single OS-wide trust store) - Keycloak's own truststore (via --truststore-paths, set up below) is what actually matters here regardless." >&2
fi

mkdir -p "$CERT_DIR"
step "Exporting dev cert for Keycloak's truststore (deploy/keycloak/certs/)"
dotnet dev-certs https -ep "$CERT_PATH" --format Pem --no-password
# Keycloak's truststore only needs the public cert, not the private key.
rm -f "$KEY_PATH"

# --- 2. Windows-only: detect LAN IP, write docker-compose.override.yml ---
OS="$(uname -s)"
LAN_IP=""

if [[ "$OS" == MINGW* || "$OS" == MSYS* || "$OS" == CYGWIN* ]]; then
  step "Detecting your machine's LAN IP (Windows/Podman networking workaround)"
  # Reuses the adapter-selection logic from this project's original
  # Windows-only script: prefer an adapter that's Up AND has a default
  # gateway - a good signal it's your real LAN/internet-facing NIC, not a
  # Hyper-V/WSL virtual switch (those usually have no gateway of their own).
  LAN_IP="$(powershell.exe -NoProfile -Command \
    "(Get-NetIPConfiguration | Where-Object { \$_.NetAdapter.Status -eq 'Up' -and \$_.IPv4DefaultGateway } | Select-Object -First 1 -ExpandProperty IPv4Address).IPAddress" \
    2>/dev/null | tr -d '\r\n')"

  if [ -n "$LAN_IP" ]; then
    echo "Using $LAN_IP"
  else
    echo "Couldn't auto-detect a LAN-facing adapter. Run 'ipconfig', find your active adapter's IPv4 address, and edit $OVERRIDE_PATH by hand (see docker-compose.override.yml.example)." >&2
  fi
elif [ "${FORCE_LAN_WORKAROUND:-}" = "1" ]; then
  step "Detecting your machine's LAN IP (FORCE_LAN_WORKAROUND=1 set)"
  case "$OS" in
    Linux*)
      LAN_IP="$(ip route get 1.1.1.1 2>/dev/null | awk '{for (i=1;i<=NF;i++) if ($i=="src") print $(i+1)}')"
      ;;
    Darwin*)
      IFACE="$(route get 1.1.1.1 2>/dev/null | awk '/interface:/{print $2}')"
      [ -n "$IFACE" ] && LAN_IP="$(ipconfig getifaddr "$IFACE" 2>/dev/null || true)"
      ;;
  esac
  [ -n "$LAN_IP" ] && echo "Using $LAN_IP" || echo "Couldn't auto-detect a LAN IP on $OS - see docker-compose.override.yml.example to do this by hand." >&2
else
  step "Skipping the LAN-IP workaround ($OS)"
  echo "This works around Podman-on-Windows specifically not forwarding host.docker.internal to arbitrary host ports (see docs/keycloak-setup.md). On $OS this likely isn't needed - host.containers.internal / host.docker.internal should reach the host directly. If you hit \"Client Metadata fetch failed\" anyway, re-run with FORCE_LAN_WORKAROUND=1."
fi

if [ -n "$LAN_IP" ]; then
  cat > "$OVERRIDE_PATH" <<EOF
services:
  keycloak:
    extra_hosts:
      - "client.dev.internal:$LAN_IP"
EOF
  echo "Wrote $OVERRIDE_PATH"
  echo "If this IP changes later (DHCP renewal), re-run this script."
else
  rm -f "$OVERRIDE_PATH"
fi

# --- 3. Start Keycloak (auto-imports the realm bundle) ---
step "Starting Keycloak via $CONTAINER_ENGINE compose"

# IMPORTANT: passing -f at all disables compose's automatic
# docker-compose.override.yml discovery - it only auto-merges the override
# when invoked with *no* -f flag at all. Since we need -f for the explicit
# path, the override has to be listed explicitly too when it exists (this
# bit an earlier version of this setup script - extra_hosts silently never
# applied).
compose_args=(compose -f "$COMPOSE_FILE")
[ -f "$OVERRIDE_PATH" ] && compose_args+=(-f "$OVERRIDE_PATH")
compose_args+=(up -d --force-recreate)

"$CONTAINER_ENGINE" "${compose_args[@]}"

step "Done"
cat <<'EOF'
Keycloak is starting at http://localhost:8080 (admin/admin), auto-importing:
  - realm 'mcpinterop'
  - the CIMD client policy/profile trusting client.dev.internal and 127.0.0.1
  - the mcp:tools client scope + audience mapper for Server
  - a test user (testuser/password) to log in with

Give it a few seconds to finish starting, then:
  dotnet run --project src/OpenID.MCPInterop.Server
  dotnet run --project src/OpenID.MCPInterop.Client

If Client's browser login fails with an invalid_scope or missing-audience
error, mcp:tools may not have auto-attached to the CIMD client - see the
troubleshooting appendix in docs/keycloak-setup.md for the one manual
fallback step.
EOF
