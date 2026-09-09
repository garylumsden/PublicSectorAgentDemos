using Azure.Monitor.OpenTelemetry.AspNetCore;
using Defra.Observability;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Defra.Tools.Mcp;

public static class McpObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddMcpObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        OpenTelemetryBuilder telemetry = services.AddOpenTelemetry();
        string? connectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            telemetry.UseAzureMonitor(options =>
            {
                options.ConnectionString = connectionString;
                options.EnableLiveMetrics = false;
            });
        }

        services.ConfigureOpenTelemetryTracerProvider(
            (_, tracing) =>
            {
                tracing
                    .ConfigureResource(resource => resource.AddService("Defra.Tools.Mcp"))
                    .AddSource(
                        DefraActivitySourceNames.AgentCore,
                        DefraActivitySourceNames.Policy,
                        DefraActivitySourceNames.Tools,
                        DefraActivitySourceNames.Audit);
            });

        return services;
    }
}
