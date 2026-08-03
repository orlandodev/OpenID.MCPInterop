# Observability - viewing traces/logs/metrics locally

`Server`, `Client`, and `Issuer` all export OpenTelemetry traces, metrics,
and structured logs via OTLP. Locally, that goes to a standalone
[Aspire Dashboard](https://aspire.dev/dashboard/standalone/) container -
not the full .NET Aspire orchestration model (AppHost/ServiceDefaults),
just its dashboard, pointed at by each project's own OTLP exporter. Nothing
about how you already run these projects (`dotnet run --project ...`) or
Keycloak (`deploy/keycloak/setup.sh`) changes - this is purely additive.

## 1. Start the dashboard

```bash
podman compose -f deploy/aspire-dashboard/docker-compose.yml up -d
```

(`docker compose` works identically if you're on Docker Desktop.)

## 2. View it

Open `http://localhost:18888`. The compose file sets
`DASHBOARD__FRONTEND__AUTHMODE=Unsecured` so there's no login token to dig
out of container logs - fine for a dashboard that's only reachable from
your own machine, not something to reuse for anything actually exposed.

You'll see a banner warning the OTLP endpoint is unsecured
(`DASHBOARD__OTLP__AUTHMODE` defaults to `Unsecured` too - anyone who can
reach port 4317 can push telemetry in). Leave it - there's a
`SuppressUnsecuredMessage` setting, but the official docs say only use it
"if an external frontdoor proxy is securing access to the endpoint," which
we don't have here. It's an accurate warning about a real (if low-risk for
a loopback-only local setup) tradeoff, not a misconfiguration to silence.

## 3. Run the services - no extra config needed by default

`Server`, `Client`, and `Issuer` all read `OTEL_EXPORTER_OTLP_ENDPOINT`
automatically via `UseOtlpExporter()`, defaulting to `http://localhost:4317`
(gRPC) when unset - exactly what the compose file above maps its OTLP port
to. Each project's `service.name` comes from
`builder.Environment.ApplicationName` in code (the assembly name), not an
env var, so `Server`/`Client`/`Issuer` already show up as three distinct
entries without extra setup. Just run them as usual:

```bash
dotnet run --project src/OpenID.MCPInterop.Server
dotnet run --project src/OpenID.MCPInterop.Client
dotnet run --project src/OpenID.MCPInterop.Issuer
```

If you ever point at a dashboard running somewhere other than
`localhost:4317` (a shared instance, a different port), override per
project via `OTEL_EXPORTER_OTLP_ENDPOINT` - e.g. in that project's
`Properties/launchSettings.json` `environmentVariables`, or in your shell
before `dotnet run`.

## What you'll see

- **No separate "Resources" page** - that's specific to full Aspire
  AppHost orchestration (it needs `ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL`
  pointing at the AppHost's resource service, which doesn't exist here by
  design). Instead, `OpenID.MCPInterop.Server`/`.Client`/`.Issuer` each show
  up as a **Resource** column/filter *within* the Traces, Structured Logs,
  and Metrics views - that's how you tell them apart, not a dedicated nav
  item.
- **Traces** - a request like `Client` calling `Server`'s `ping` tool shows
  up as one connected span waterfall across both services (`AddAspNetCoreInstrumentation`
  covers the incoming side, `AddHttpClientInstrumentation` the outgoing
  side, trace context propagates automatically over the HTTP headers
  between them). Once the EMA leg lands, `Client` -> `Issuer` and
  `Issuer` -> Keycloak's JWKS fetch will show up the same way.
- **MCP-level detail, not just "an HTTP POST happened"** - `Server` and
  `Client` both register the MCP C# SDK's own `ActivitySource`
  (`"Experimental.ModelContextProtocol"` - the SDK's own name for it,
  despite how stable everything else here has been), which emits spans for
  tool/method dispatch. On top of that, `DemoTools.Ping()` (`Server`) and
  the `ping` call site (`Client`) both tag the current span with
  `mcp.tool.name` - so you can see *which tool* a call was for without the
  request/response body being captured (see "Why you can't see request
  bodies" below).
- **Structured logs** - correlated to whichever trace produced them
  (`TraceId`/`SpanId` attached automatically).
- **Metrics** - request counts/latencies from the ASP.NET Core and
  HttpClient instrumentation.

## Why you can't see full request/response bodies

Deliberate, not a gap: the ASP.NET Core/HttpClient instrumentation captures
span metadata (method, route, status, timing) but never bodies, by design -
partly size, but mainly that bodies in this repo routinely contain bearer
tokens and ID tokens, and auto-capturing those into telemetry would mean
logging credentials by default. The `mcp.tool.name` tagging above is the
targeted alternative: pull out the *specific*, safe detail that's actually
useful (which tool, which claim) rather than capturing everything. If you
add more of this later, keep doing it selectively - never tag/log a raw
token or the full body of an OAuth token response.

## Notes

- This is local-dev tooling, not part of the deployed app - nothing here
  needs to ship anywhere `Server`/`Client`/`Issuer` actually run in a real
  environment.
- Not wired up yet: custom `Meter`/metric instrumentation for business
  counters (e.g. "ID-JAG exchanges attempted/succeeded" once the EMA leg
  exists), and `Issuer` has no MCP `ActivitySource`/tool-level tagging since
  it doesn't use the MCP SDK - if its own `/token` logic needs span-level
  detail later, that'll be a manually-created `ActivitySource`, not a
  registration of an existing one.
