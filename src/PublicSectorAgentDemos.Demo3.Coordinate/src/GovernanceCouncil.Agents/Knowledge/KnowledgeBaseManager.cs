namespace GovernanceCouncil.Agents.Knowledge;

using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.KnowledgeBases.Models;
using GovernanceCouncil.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Provisions the Foundry IQ Knowledge Base data-plane objects (a Bing-grounded Web Knowledge
/// Source + the Knowledge Base that references it) on Azure AI Search.
///
/// An idempotent CreateOrUpdate is issued on every app
/// boot (called from <c>CouncilOrchestrator.ProvisionAgentsAsync</c> path / Program.cs startup),
/// so <c>azd up</c> provisions infra and the first app boot self-provisions the KS + KB.
///
/// SDK-first: uses <see cref="SearchIndexClient"/> from Azure.Search.Documents 12.1.0-beta.1 with
/// the 2026-05-01-preview service version (Web KS + answer synthesis support). The KB name MUST
/// equal the RemoteTool project connection target (<c>KNOWLEDGE_BASE_CONNECTION_NAME</c>) so the
/// agents' MCP tool resolves to it.
/// </summary>
public sealed class KnowledgeBaseManager
{
    public const string WebKnowledgeSourceName = "gc-web-ks";
    public const string DefaultKnowledgeBaseName = "kbgcknowledgebase";

    // Authoritative source domains the council personas are instructed to cite, taken from the active
    // scenario. The Web KS scopes Bing grounding to these (subpages included). Empty ⇒ KB skipped.
    private static IReadOnlyList<string> AllowedDomains => Scenario.Current.GroundingDomains;

    private readonly TokenCredential _credential;
    private readonly string? _searchEndpoint;
    private readonly Uri? _aoaiResourceUri;
    private readonly string _chatModelDeployment;
    private readonly string _knowledgeBaseName;
    private readonly ILogger<KnowledgeBaseManager> _logger;

    public KnowledgeBaseManager(
        TokenCredential credential,
        string? searchEndpoint,
        string? aiServicesEndpoint,
        string chatModelDeployment,
        string knowledgeBaseName = DefaultKnowledgeBaseName,
        string? azureOpenAIEndpoint = null,
        ILogger<KnowledgeBaseManager>? logger = null)
    {
        _credential = credential;
        _searchEndpoint = string.IsNullOrWhiteSpace(searchEndpoint) ? null : searchEndpoint.Trim();
        _aoaiResourceUri = ResolveAoaiResourceUri(azureOpenAIEndpoint, aiServicesEndpoint);
        _chatModelDeployment = chatModelDeployment;
        _knowledgeBaseName = string.IsNullOrWhiteSpace(knowledgeBaseName) ? DefaultKnowledgeBaseName : knowledgeBaseName.Trim();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeBaseManager>.Instance;
    }

    /// <summary>True when the Search service endpoint is configured (KB provisioning possible).</summary>
    public bool IsConfigured => _searchEndpoint is not null;

    /// <summary>
    /// Idempotently creates/updates the Web Knowledge Source and the Knowledge Base.
    /// Graceful: if Search isn't configured, logs and returns so the demo still runs.
    /// </summary>
    public async Task EnsureKnowledgeBaseAsync(CancellationToken ct = default)
    {
        if (_searchEndpoint is null)
        {
            _logger.LogInformation("Foundry IQ knowledge base not configured (no SEARCH_SERVICE_ENDPOINT) — skipping KB provisioning");
            return;
        }

        if (AllowedDomains.Count == 0)
        {
            _logger.LogInformation("No scenario grounding domains configured — skipping Foundry IQ knowledge base provisioning");
            return;
        }

        var options = new SearchClientOptions(SearchClientOptions.ServiceVersion.V2026_05_01_Preview);
        var indexClient = new SearchIndexClient(new Uri(_searchEndpoint), _credential, options);

        // 1) Web Knowledge Source — Bing-grounded, scoped to the scenario's authoritative domains.
        var domains = new WebKnowledgeSourceDomains();
        foreach (var domain in AllowedDomains)
        {
            domains.AllowedDomains.Add(new WebKnowledgeSourceDomain(domain) { IncludeSubpages = true });
        }

        var webKs = new WebKnowledgeSource(WebKnowledgeSourceName)
        {
            Description = $"Bing-grounded web knowledge source scoped to the scenario's authoritative domains ({string.Join(", ", AllowedDomains)}).",
            WebParameters = new WebKnowledgeSourceParameters
            {
                Domains = domains
            }
        };

        await indexClient.CreateOrUpdateKnowledgeSourceAsync(webKs, onlyIfUnchanged: false, ct);
        _logger.LogInformation("Web knowledge source {KsName} provisioned (domains: {Domains})", WebKnowledgeSourceName, string.Join(", ", AllowedDomains));

        // 2) Knowledge Base — references the Web KS, synthesizes cited answers using the FAST model.
        var kb = new KnowledgeBase(_knowledgeBaseName, [new KnowledgeSourceReference(WebKnowledgeSourceName)])
        {
            Description = "Foundry IQ knowledge base. Grounds council personas in the scenario's configured authoritative sources with cited answer synthesis.",
            RetrievalInstructions = "Retrieve current, authoritative guidance from the configured sources relevant to the query.",
            AnswerInstructions = "Answer only from retrieved sources; include citations using the returned ref ids.",
            OutputMode = KnowledgeRetrievalOutputMode.AnswerSynthesis,
            RetrievalReasoningEffort = new KnowledgeRetrievalLowReasoningEffort()
        };

        if (_aoaiResourceUri is not null)
        {
            var aoai = new AzureOpenAIVectorizerParameters
            {
                ResourceUri = _aoaiResourceUri,
                DeploymentName = _chatModelDeployment,
                ModelName = _chatModelDeployment
            };
            kb.Models.Add(new KnowledgeBaseAzureOpenAIModel(aoai));
        }
        else
        {
            _logger.LogWarning("AZURE_AI_SERVICES_ENDPOINT not set — KB created without a chat model; answer synthesis will be unavailable");
        }

        await indexClient.CreateOrUpdateKnowledgeBaseAsync(kb, onlyIfUnchanged: false, ct);
        _logger.LogInformation("Knowledge base {KbName} provisioned (model: {Model})", _knowledgeBaseName, _chatModelDeployment);
    }

    /// <summary>
    /// Resolves the Azure OpenAI resource URI (https://&lt;account&gt;.openai.azure.com/) used for
    /// KB web summarization / answer synthesis. Prefers an explicitly-configured endpoint
    /// (AZURE_OPENAI_ENDPOINT); otherwise derives it from the AI Services endpoint
    /// (https://&lt;account&gt;.cognitiveservices.azure.com/). Returns null when neither is set.
    /// </summary>
    private static Uri? ResolveAoaiResourceUri(string? azureOpenAIEndpoint, string? aiServicesEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(azureOpenAIEndpoint))
            return new Uri(azureOpenAIEndpoint.Trim());

        if (string.IsNullOrWhiteSpace(aiServicesEndpoint))
            return null;

        var host = new Uri(aiServicesEndpoint).Host;
        if (host.Contains(".openai.azure.com", StringComparison.OrdinalIgnoreCase))
            return new Uri($"https://{host}/");

        var account = host.Split('.')[0];
        return new Uri($"https://{account}.openai.azure.com/");
    }
}
