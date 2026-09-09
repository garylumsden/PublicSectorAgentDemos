using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Defra.AgentCore;
using Defra.Audit;
using Defra.Contracts.FloodSupport;
using Demo2.Web.Agent;
using Demo2.Web.Components;
using Demo2.Web.Configuration;
using Demo2.Web.Domain;
using Demo2.Web.Observability;
using Demo2.Web.Persistence;
using Demo2.Web.Security;
using Demo2.Web.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Demo2.Web;

public static class Program
{
    public static void Main(string[] args)
    {
        WebApplication app = BuildApplication(args);
        app.Run();
    }

    public static WebApplication BuildApplication(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.Services
            .AddOptions<Demo2Options>()
            .Bind(builder.Configuration.GetSection(Demo2Options.SectionName))
            .PostConfigure(options =>
                Demo2EnvironmentConfiguration.Apply(
                    options,
                    builder.Configuration,
                    builder.Environment))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<Demo2Options>, Demo2OptionsValidator>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<Demo2ConfigurationPathResolver>();
        builder.Services.AddSingleton<IWelfareRuleProvider, WelfareRuleProvider>();
        builder.Services.AddSingleton<IDemo2ToolPolicyProvider, Demo2ToolPolicyProvider>();
        builder.Services.AddSingleton<ComplaintInputGuard>();
        builder.Services.AddSingleton<WelfareDeterministicPolicy>();
        builder.Services.AddSingleton<Demo2AuditTrail>();

        builder.Services.AddSingleton<TokenCredential>(_ =>
            DefaultAzureCredentialFactory.Create(
                builder.Configuration["AZURE_TENANT_ID"],
                builder.Configuration["AZURE_CLIENT_ID"]));

        builder.Services.AddSingleton<InMemoryDemo2StateStore>();
        builder.Services.AddSingleton(sp =>
        {
            Demo2PersistenceOptions persistence =
                sp.GetRequiredService<IOptions<Demo2Options>>().Value.Persistence;
            return new CosmosClient(
                persistence.CosmosEndpoint,
                sp.GetRequiredService<TokenCredential>(),
                new CosmosClientOptions
                {
                    UseSystemTextJsonSerializerWithOptions =
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        PropertyNameCaseInsensitive = false,
                        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
                    }
                });
        });
        builder.Services.AddSingleton<CosmosDemo2StateStore>();
        builder.Services.AddSingleton<IWelfareCaseStore>(
            sp => ResolveStateStore<IWelfareCaseStore>(sp));
        builder.Services.AddSingleton<IConversationStore>(
            sp => ResolveStateStore<IConversationStore>(sp));
        builder.Services.AddSingleton<IApprovalStore>(
            sp => ResolveStateStore<IApprovalStore>(sp));
        builder.Services.AddSingleton<IInspectorDecisionStore>(
            sp => ResolveStateStore<IInspectorDecisionStore>(sp));

        builder.Services.AddSingleton<SyntheticContractWelfareAgentClient>();
        builder.Services.AddSingleton<FloodSupportReservationSimulator>();
        builder.Services.AddSingleton<AzureWelfareAgentClient>();
        builder.Services.AddSingleton<IWelfareAgentClient>(sp =>
        {
            Demo2AgentOptions agent =
                sp.GetRequiredService<IOptions<Demo2Options>>().Value.Agent;
            return string.Equals(agent.Mode, Demo2AgentModes.Azure, StringComparison.Ordinal)
                ? sp.GetRequiredService<AzureWelfareAgentClient>()
                : sp.GetRequiredService<SyntheticContractWelfareAgentClient>();
        });
        builder.Services.AddSingleton<InMemoryWelfareToolboxClient>();
        builder.Services.AddSingleton<McpWelfareToolboxClient>();
        builder.Services.AddSingleton<IWelfareToolboxClient>(services =>
        {
            Demo2AgentOptions agent =
                services.GetRequiredService<IOptions<Demo2Options>>().Value.Agent;
            return string.Equals(agent.Mode, Demo2AgentModes.Azure, StringComparison.Ordinal)
                ? services.GetRequiredService<McpWelfareToolboxClient>()
                : services.GetRequiredService<InMemoryWelfareToolboxClient>();
        });
        builder.Services.AddSingleton<WelfareAssessmentService>();
        builder.Services.AddSingleton<Demo2AdminWorkflowService>();
        builder.Services.AddSingleton<IAppendOnlyAuditWriter>(CreateAuditWriter);

        builder.Services.AddDemo2Observability(builder.Configuration);
        builder.Services.AddHttpClient();
        builder.Services.AddFluentUIComponents();
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        WebApplication app = builder.Build();
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        if (!app.Environment.IsEnvironment("Testing"))
        {
            app.UseHttpsRedirection();
        }

        app.UseAntiforgery();

        app.MapGet(
                "/health",
                (IOptions<Demo2Options> options) =>
                    Results.Ok(new
                    {
                        status = "Healthy",
                        service = "cross-government-flood-support",
                        actionMode = "approval-controlled",
                        agent = options.Value.Agent.Name,
                        agentMode = options.Value.Agent.Mode,
                        persistence = options.Value.Persistence.Provider
                    }));

        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return app;
    }

    private static TStore ResolveStateStore<TStore>(IServiceProvider services)
        where TStore : class
    {
        Demo2PersistenceOptions persistence =
            services.GetRequiredService<IOptions<Demo2Options>>().Value.Persistence;
        object store = string.Equals(
            persistence.Provider,
            Demo2PersistenceProviders.Cosmos,
            StringComparison.Ordinal)
            ? services.GetRequiredService<CosmosDemo2StateStore>()
            : services.GetRequiredService<InMemoryDemo2StateStore>();
        return (TStore)store;
    }

    private static IAppendOnlyAuditWriter CreateAuditWriter(IServiceProvider services)
    {
        Demo2AuditOptions audit = services.GetRequiredService<IOptions<Demo2Options>>().Value.Audit;
        IHostEnvironment environment = services.GetRequiredService<IHostEnvironment>();
        string path = Demo2AuditPathResolver.Resolve(
            audit.Path,
            environment.EnvironmentName,
            Environment.GetEnvironmentVariable("HOME"),
            AppContext.BaseDirectory);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream stream = new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            });
        return new JsonLinesAuditWriter(stream);
    }
}
