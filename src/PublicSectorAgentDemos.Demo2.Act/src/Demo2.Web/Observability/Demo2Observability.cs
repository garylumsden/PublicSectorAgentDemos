using Azure.Monitor.OpenTelemetry.AspNetCore;
using Defra.Observability;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Demo2.Web.Observability;

public static class Demo2ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddDemo2Observability(
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
                    .ConfigureResource(resource => resource.AddService("Demo2.Web"))
                    .AddSource(
                        DefraActivitySourceNames.AgentCore,
                        DefraActivitySourceNames.Policy,
                        DefraActivitySourceNames.Tools,
                        DefraActivitySourceNames.Audit);
            });

        return services;
    }
}
