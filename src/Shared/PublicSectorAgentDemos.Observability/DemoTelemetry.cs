using System.Diagnostics;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace PublicSectorAgentDemos.Observability;

public static class DemoTelemetry
{
    public const string ActivitySourceName = "PublicSectorAgentDemos";

    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    public static IServiceCollection AddDemoObservability(
        this IServiceCollection services,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        string configuredServiceName =
            Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") is { } value &&
            !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : serviceName;
        OpenTelemetry.OpenTelemetryBuilder telemetry = services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(configuredServiceName))
            .WithTracing(tracing => tracing
                .AddSource(ActivitySourceName)
                .AddSource("Azure.AI.Projects.*")
                .AddSource("Azure.AI.OpenAI.*")
                .AddSource("Microsoft.Extensions.AI.*")
                .AddSource("OpenAI.*"));

        string? applicationInsights =
            Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(applicationInsights))
        {
            if (!applicationInsights.Contains(
                    "InstrumentationKey=",
                    StringComparison.OrdinalIgnoreCase)
                || !applicationInsights.Contains(
                    "IngestionEndpoint=https://",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "APPLICATIONINSIGHTS_CONNECTION_STRING is invalid.");
            }

            AppContext.SetSwitch("Azure.Experimental.EnableGenAITracing", true);
            DefaultAzureCredentialOptions credentialOptions = new();
            string? clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                credentialOptions.ManagedIdentityClientId = Guid.TryParse(
                    clientId,
                    out Guid parsedClientId)
                    ? parsedClientId.ToString("D")
                    : throw new InvalidOperationException(
                        "AZURE_CLIENT_ID must be a GUID when Application Insights is enabled.");
            }

            telemetry.UseAzureMonitor(options =>
            {
                options.ConnectionString = applicationInsights;
                options.Credential = new DefaultAzureCredential(credentialOptions);
            });
            return services;
        }

        string? endpointValue =
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(endpointValue))
        {
            if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out Uri? endpoint)
                || endpoint.Scheme is not ("http" or "https"))
            {
                throw new InvalidOperationException(
                    "OTEL_EXPORTER_OTLP_ENDPOINT must be an absolute HTTP or HTTPS URI.");
            }

            telemetry.WithTracing(tracing =>
                tracing.AddOtlpExporter(options => options.Endpoint = endpoint));
        }

        return services;
    }

    public static Activity? StartActivity(
        string name,
        ActivityKind kind = ActivityKind.Internal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ActivitySource.StartActivity(name, kind);
    }
}
