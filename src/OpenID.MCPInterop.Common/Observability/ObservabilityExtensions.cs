using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OpenID.MCPInterop.Common.Observability;

/// <summary>
/// Traces/metrics/logs exported via OTLP to a standalone Aspire Dashboard
/// container (see docs/observability.md). Shared by Client/Server/Issuer.
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>
    /// <paramref name="includeMcpActivitySource"/>: the MCP C# SDK emits its
    /// own spans (tool/method dispatch) under "Experimental.ModelContextProtocol" -
    /// set true for Client/Server, which act as MCP endpoints; Issuer doesn't
    /// speak MCP, so it stays false there.
    /// </summary>
    public static IServiceCollection AddInteropObservability(
        this IServiceCollection services,
        string serviceName,
        bool includeMcpActivitySource = false)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                if (includeMcpActivitySource)
                {
                    tracing.AddSource("Experimental.ModelContextProtocol");
                }
            })
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithLogging(configureBuilder: static _ => { }, configureOptions: static options => options.IncludeScopes = true)
            .UseOtlpExporter();

        return services;
    }
}
