using Azure.AI.OpenAI;
using Azure.AI.Projects;
using Azure.Identity;
using GovernanceCouncil.Agents.Ingestion;
using GovernanceCouncil.Agents.Knowledge;
using GovernanceCouncil.Agents.Nexus;
using GovernanceCouncil.Agents.Orchestration;
using GovernanceCouncil.Agents.Provisioning;
using GovernanceCouncil.Agents.Runtime;
using GovernanceCouncil.Core.Interfaces;
using GovernanceCouncil.Core.Models;
using GovernanceCouncil.Data;
using GovernanceCouncil.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using GovernanceCouncil.Web.Components;
using GovernanceCouncil.Web.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Serve build-time static web assets (blazor.web.js, the scoped *.styles.css bundle, component
// .razor.js) in ALL environments — not just Development. Without this, running from build output
// in a non-Development environment (e.g. `dotnet GovernanceCouncil.Web.dll` with no
// ASPNETCORE_ENVIRONMENT set) makes MapStaticAssets look for the files physically in wwwroot and
// 500 with "Static Web Assets are not enabled". Safe in published output (no-op when no manifest).
builder.WebHost.UseStaticWebAssets();

// Load .env file if present (for local dev after azd provision)
DotEnvLoader.Load(Path.Combine(builder.Environment.ContentRootPath, ".env"));

// Load the active scenario (branding, grounding domains, council composition) from config/scenario.json.
// Absent ⇒ neutral defaults (generic branding + empty council) so the template runs unconfigured.
Scenario.Initialise();

// Build a shared DefaultAzureCredential with tenant ID pinned (if set)
var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
var credentialOptions = new DefaultAzureCredentialOptions();
if (!string.IsNullOrWhiteSpace(tenantId))
    credentialOptions.TenantId = tenantId;
var credential = new DefaultAzureCredential(credentialOptions);

// Check if Azure resources are configured
var cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_DB_ENDPOINT");
var storageAccountName = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_NAME");
var projectEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_FOUNDRY_ENDPOINT");
var isConfigured = !string.IsNullOrEmpty(cosmosEndpoint)
    && !string.IsNullOrEmpty(storageAccountName)
    && !string.IsNullOrEmpty(projectEndpoint);

if (!isConfigured)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine();
    Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║  Azure resources not configured.                            ║");
    Console.WriteLine("║                                                             ║");
    Console.WriteLine("║  Run 'azd up' from the Governance-Council/ directory to     ║");
    Console.WriteLine("║  provision infrastructure. This creates a .env file with     ║");
    Console.WriteLine("║  all required endpoints.                                    ║");
    Console.WriteLine("║                                                             ║");
    Console.WriteLine("║  Or copy .env.template to .env and fill in values manually. ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
    Console.ResetColor();
    Console.WriteLine();
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddFluentUIComponents();
builder.Services.AddHttpClient();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IDeliberationNotifier, GovernanceCouncil.Web.Services.DeliberationNotifier>();

// Application Insights telemetry
var appInsightsConnStr = Environment.GetEnvironmentVariable("APPINSIGHTS_CONNECTION_STRING");
if (!string.IsNullOrWhiteSpace(appInsightsConnStr))
{
    builder.Services.AddApplicationInsightsTelemetry(options =>
    {
        options.ConnectionString = appInsightsConnStr;
    });
}

// Register Azure-dependent services only if configured
if (isConfigured)
{
    builder.Services.AddGovernanceCouncilData(cosmosEndpoint!, storageAccountName!, credential);

    var aiProjectClient = new AIProjectClient(
        endpoint: new Uri(projectEndpoint!),
        tokenProvider: credential);
    builder.Services.AddSingleton(aiProjectClient);

    builder.Services.AddSingleton<NexusAnalystService>(sp =>
    {
        // Embeddings use the Azure OpenAI data-plane endpoint (same account that serves the chat models).
        var nexusAoaiEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") is { Length: > 0 } e
            ? e : Environment.GetEnvironmentVariable("AZURE_AI_SERVICES_ENDPOINT");
        return new NexusAnalystService(
            sp.GetRequiredService<AIProjectClient>(),
            new AzureOpenAIClient(new Uri(nexusAoaiEndpoint!), credential),
            sp.GetRequiredService<IAssessmentStore>(),
            sp.GetRequiredService<INexusStore>(),
            logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<NexusAnalystService>());
    });
    // Foundry IQ knowledge base data-plane provisioning (Web KS + KB via Azure.Search.Documents).
    var searchServiceEndpoint = Environment.GetEnvironmentVariable("SEARCH_SERVICE_ENDPOINT");
    var aiServicesEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_SERVICES_ENDPOINT");
    var azureOpenAIEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
    var kbConnectionName = Environment.GetEnvironmentVariable("KNOWLEDGE_BASE_CONNECTION_NAME");
    var fastModel = Environment.GetEnvironmentVariable("COUNCIL_FAST_MODEL") ?? "gpt-5-mini";
    builder.Services.AddSingleton<KnowledgeBaseManager>(sp =>
        new KnowledgeBaseManager(
            credential,
            searchServiceEndpoint,
            aiServicesEndpoint,
            fastModel,
            string.IsNullOrWhiteSpace(kbConnectionName) ? KnowledgeBaseManager.DefaultKnowledgeBaseName : kbConnectionName!,
            azureOpenAIEndpoint,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<KnowledgeBaseManager>()));
    var webIqApiKey = Environment.GetEnvironmentVariable("WEBIQ_API_KEY");
    builder.Services.AddSingleton<AgentCache>();

    // Foundry runtime (default): provisions + fetches the Foundry Prompt Agents.
    builder.Services.AddSingleton<FoundryCouncilRuntime>(sp =>
        new FoundryCouncilRuntime(
            sp.GetRequiredService<AIProjectClient>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
            credential,
            projectEndpoint!,
            sp.GetRequiredService<AgentCache>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<FoundryCouncilRuntime>()));

    // Local MAF runtime: builds ChatClientAgents on the Foundry chat models + in-proc grounding.
    var aoaiEndpoint = string.IsNullOrWhiteSpace(azureOpenAIEndpoint)
        ? aiServicesEndpoint : azureOpenAIEndpoint;
    builder.Services.AddSingleton<MafCouncilRuntime>(sp =>
        new MafCouncilRuntime(
            new Uri(aoaiEndpoint!),
            credential,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
            searchServiceEndpoint,
            string.IsNullOrWhiteSpace(kbConnectionName) ? KnowledgeBaseManager.DefaultKnowledgeBaseName : kbConnectionName!,
            webIqApiKey,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<MafCouncilRuntime>()));

    builder.Services.AddSingleton<CouncilRuntimeProvider>();

    // Runtime content-filter toggle: updates the account RAI policy's Violence threshold per
    // deliberation (control plane). Uses the shared credential + a pooled HttpClient.
    builder.Services.AddSingleton<GovernanceCouncil.Agents.Provisioning.RaiPolicyManager>(sp =>
        new GovernanceCouncil.Agents.Provisioning.RaiPolicyManager(
            credential,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<GovernanceCouncil.Agents.Provisioning.RaiPolicyManager>()));

    builder.Services.AddSingleton<CouncilOrchestrator>(sp =>
        new CouncilOrchestrator(
            sp.GetRequiredService<IDeliberationNotifier>(),
            sp.GetRequiredService<IDossierStore>(),
            sp.GetRequiredService<IDossierBlobService>(),
            sp.GetRequiredService<IDeliberationStore>(),
            sp.GetRequiredService<IAssessmentStore>(),
            sp.GetRequiredService<CouncilRuntimeProvider>(),
            sp.GetRequiredService<NexusAnalystService>(),
            sp.GetRequiredService<GovernanceCouncil.Agents.Provisioning.RaiPolicyManager>(),
            sp.GetRequiredService<KnowledgeBaseManager>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<CouncilOrchestrator>()));
    builder.Services.AddSingleton<DossierIngestionService>(sp =>
        new DossierIngestionService(
            sp.GetRequiredService<IDossierStore>(),
            sp.GetRequiredService<IDossierBlobService>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<DossierIngestionService>()));
}

var app = builder.Build();

// Agent provisioning enabled for deployment test.
// Set GovernanceCouncil:ProvisionAgentsOnStartup to false to disable.
if (isConfigured && builder.Configuration.GetValue("GovernanceCouncil:ProvisionAgentsOnStartup", true))
{
    var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    // KB and agent provisioning are ISOLATED. A Foundry IQ knowledge-base hiccup (e.g. the KB
    // already exists) must NOT block agent re-provisioning — otherwise every agent stays stuck on
    // its first-ever version (e.g. a stale prompt) because this whole block aborted.
    try
    {
        // Foundry IQ only: self-provision the KB (Web KS + KB). Web IQ needs no Search/KB.
        if (Grounding.Active == Grounding.Provider.FoundryIq)
        {
            var knowledgeBaseManager = app.Services.GetRequiredService<KnowledgeBaseManager>();
            await knowledgeBaseManager.EnsureKnowledgeBaseAsync();
        }
        else
        {
            startupLogger.LogInformation("Grounding provider Web IQ — skipping Foundry IQ knowledge base provisioning");
        }
    }
    catch (Exception ex)
    {
        startupLogger.LogError(ex, "Foundry IQ knowledge base provisioning failed; agents will still be provisioned (grounding may be degraded until the KB is available)");
    }

    try
    {
        var orchestrator = app.Services.GetRequiredService<CouncilOrchestrator>();
        await orchestrator.ProvisionAgentsAsync();
        startupLogger.LogInformation("Council agents ready ({Runtime})", AgentRuntime.DisplayName(AgentRuntime.Active));
    }
    catch (Exception ex)
    {
        startupLogger.LogError(ex, "Agent provisioning failed. The web app will start but deliberations may fail until agents are provisioned. This can happen if RBAC roles haven't propagated yet — try restarting in a few minutes");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHub<DeliberationHub>("/hubs/deliberation");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
