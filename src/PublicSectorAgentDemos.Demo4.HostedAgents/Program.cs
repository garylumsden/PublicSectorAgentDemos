#pragma warning disable AAIP001
#pragma warning disable OPENAI001

using System.Diagnostics;
using System.Text.Json;
using Azure.AI.AgentServer.Core;
using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using PublicSectorAgentDemos.Contracts.Demo4;

namespace PublicSectorAgentDemos.Demo4.HostedAgents;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Uri projectEndpoint = RequiredProjectEndpoint();
        string deployment = RequiredEnvironment("AZURE_AI_MODEL_DEPLOYMENT_NAME");
        string toolboxName = RequiredEnvironment("TOOLBOX_NAME");
        TokenCredential credential = CreateCredential(
            RequiredEnvironment("AZURE_TENANT_ID"),
            Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"));

        await using FoundryToolboxSkillsConnection skillsConnection =
            await FoundryToolboxSkillsConnection.ConnectAsync(
                projectEndpoint,
                toolboxName,
                credential).ConfigureAwait(false);
        AIProjectClient projectClient = new(projectEndpoint, credential);
        string fixtureRoot = Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "hosted",
            "v2");
        CaseScenarioCatalog scenarioCatalog = new(Path.Combine(
            fixtureRoot,
            "fixture-set.json"));
        CaseContextCatalog contextCatalog = new(Path.Combine(
            fixtureRoot,
            "context-fixture-set.json"));
        CaseInvestigationToolProvider investigationTools = new(
            scenarioCatalog,
            contextCatalog);
        ResponsesMemoryReadTurnLimiter memoryReadLimiter = new();
        FoundryMemorySearchTool memorySearch = new(
            new FoundryMemorySearchClient(projectClient),
            TimeProvider.System,
            memoryReadLimiter);

        var skillsProvider = new AgentSkillsProviderBuilder()
            .UseMcpSkills(skillsConnection.Client)
            .Build();
        ChatClientAgentOptions agentOptions = new()
        {
            Name = "cross-government-control-investigator",
            Description = "Preview agent for cross-government control investigations.",
            ChatOptions = new()
            {
                ModelId = deployment,
                Instructions = HostedAgentInstructions.Build(),
                RawRepresentationFactory = _ => new CreateResponseOptions
                {
                    ReasoningOptions = new()
                    {
                        ReasoningEffortLevel = ResponseReasoningEffortLevel.Low
                    },
                    ParallelToolCallsEnabled = false
                },
                Tools =
                [
                    AIFunctionFactory.Create(
                        memorySearch.SearchAsync,
                        Demo4MemoryContract.SearchToolName,
                        FoundryMemorySearchTool.ToolDescription,
                        AIJsonUtilities.DefaultOptions),
                    AIFunctionFactory.Create(
                        investigationTools.AssessAsync,
                        CaseInvestigationToolProvider.AssessmentToolName,
                        "Assess one configured cross-government control case.",
                        AIJsonUtilities.DefaultOptions),
                    AIFunctionFactory.Create(
                        investigationTools.SearchAsync,
                        CaseInvestigationToolProvider.SearchToolName,
                        "Return bounded case framing. The result is not canonical evidence.",
                        AIJsonUtilities.DefaultOptions)
                ]
            },
            AIContextProviders = [skillsProvider]
        };
        AIAgent agent = projectClient.AsAIAgent(agentOptions);

        var builder = AgentHost.CreateBuilder(args);
        builder.Services.AddFoundryResponses(agent);
        builder.Services.AddFoundryToolboxes(credential, toolboxName);
        builder.RegisterProtocol(
            "responses",
            endpoints => endpoints.MapFoundryResponses());

        var app = builder.Build();
        HostedAgentInputGuard guard = new();
        ILogger logger = app.App.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Demo4.HostedAgent");
        app.App.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                string traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
                logger.LogError("Hosted agent request failed. Trace {TraceId}.", traceId);
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    JsonSerializer.Serialize(new
                    {
                        error = new
                        {
                            code = "hosted_agent.unhandled",
                            message = "The hosted agent could not complete the request.",
                            traceId
                        }
                    }),
                    context.RequestAborted).ConfigureAwait(false);
            });
        });
        app.App.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            bool isResponsesTurn = HttpMethods.IsPost(context.Request.Method) &&
                context.Request.Path.Value?.EndsWith(
                    "/responses",
                    StringComparison.OrdinalIgnoreCase) == true;
            using IDisposable? memoryTurn = isResponsesTurn
                ? memoryReadLimiter.BeginTurn()
                : null;
            if (isResponsesTurn)
            {
                context.Request.EnableBuffering();
                using MemoryStream buffer = new();
                await context.Request.Body.CopyToAsync(
                    buffer,
                    context.RequestAborted).ConfigureAwait(false);
                context.Request.Body.Position = 0;
                HostedAgentGuardDecision decision = guard.Screen(buffer.ToArray());
                if (!decision.IsAllowed)
                {
                    string envelopeShape = "unavailable";
                    string inputShape = "unavailable";
                    try
                    {
                        using JsonDocument rejected = JsonDocument.Parse(buffer.ToArray());
                        envelopeShape = string.Join(
                            ",",
                            rejected.RootElement.EnumerateObject()
                                .Select(property => $"{property.Name}:{property.Value.ValueKind}"));
                        if (rejected.RootElement.TryGetProperty("input", out JsonElement rejectedInput) &&
                            rejectedInput.ValueKind == JsonValueKind.Array)
                        {
                            inputShape = string.Join(
                                ",",
                                rejectedInput.EnumerateArray()
                                    .Where(item => item.ValueKind == JsonValueKind.Object)
                                    .SelectMany(item => item.EnumerateObject())
                                    .Select(property => $"{property.Name}:{property.Value.ValueKind}"));
                        }
                    }
                    catch (JsonException)
                    {
                    }

                    logger.LogWarning(
                        "Rejected hosted input {Code}. Envelope shape: {EnvelopeShape}. Input shape: {InputShape}.",
                        decision.Code,
                        envelopeShape,
                        inputShape);
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        JsonSerializer.Serialize(new
                        {
                            error = new
                            {
                                code = decision.Code,
                                message = "The request failed the input policy."
                            }
                        }),
                        context.RequestAborted).ConfigureAwait(false);
                    return;
                }
            }

            await next().ConfigureAwait(false);
        });
        app.App.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            service = "Demo4.HostedAgent",
            preview = true
        }));
        app.Run();
    }

    private static TokenCredential CreateCredential(string tenantId, string? clientId)
    {
        if (!Guid.TryParse(tenantId, out Guid parsedTenant) || parsedTenant == Guid.Empty)
        {
            throw new InvalidOperationException("AZURE_TENANT_ID must be a non-empty GUID.");
        }

        DefaultAzureCredentialOptions options = new()
        {
            TenantId = parsedTenant.ToString("D")
        };
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            if (!Guid.TryParse(clientId, out Guid parsedClient) || parsedClient == Guid.Empty)
            {
                throw new InvalidOperationException("AZURE_CLIENT_ID must be a non-empty GUID.");
            }

            options.ManagedIdentityClientId = parsedClient.ToString("D");
        }

        return new DefaultAzureCredential(options);
    }

    private static Uri RequiredProjectEndpoint()
    {
        Uri endpoint = new(RequiredEnvironment("FOUNDRY_PROJECT_ENDPOINT"));
        return endpoint.Scheme == Uri.UriSchemeHttps &&
            endpoint.Port == 443 &&
            endpoint.IdnHost.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase) &&
            endpoint.AbsolutePath.StartsWith("/api/projects/", StringComparison.Ordinal) &&
            string.IsNullOrEmpty(endpoint.Query) &&
            string.IsNullOrEmpty(endpoint.Fragment)
            ? endpoint
            : throw new InvalidOperationException(
                "FOUNDRY_PROJECT_ENDPOINT must be a valid Foundry project endpoint.");
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value &&
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException($"{name} is required.");
}
