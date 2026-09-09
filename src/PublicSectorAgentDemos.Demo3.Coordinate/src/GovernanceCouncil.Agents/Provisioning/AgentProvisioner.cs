namespace GovernanceCouncil.Agents.Provisioning;

using System.Text.Json;
using GovernanceCouncil.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Provisions the Foundry Prompt Agents for every council member: loads each persona's embedded
/// system prompt and the Foundry IQ knowledge-base MCP tool, and creates a
/// new agent version via REST (<c>@latest</c> re-routes). Pulled out of the orchestrator so the
/// orchestrator stays a thin coordinator and the Foundry plumbing lives in one place.
/// </summary>
internal sealed class AgentProvisioner
{
    private const string KbMcpApiVersion = "2026-05-01-preview";

    private readonly HttpClient? _httpClient;
    private readonly Azure.Core.TokenCredential? _credential;
    private readonly string? _projectEndpoint;
    private readonly ILogger _logger;

    private string? _knowledgeBaseConnectionName;
    private string? _searchEndpoint;
    private string? _webIqConnectionName;
    private string? _webIqMcpUrl;
    private string? _raiPolicyName;

    public AgentProvisioner(
        HttpClient? httpClient,
        Azure.Core.TokenCredential? credential,
        string? projectEndpoint,
        ILogger logger)
    {
        _httpClient = httpClient;
        _credential = credential;
        _projectEndpoint = projectEndpoint;
        _logger = logger;
    }

    /// <summary>
    /// Provisions every council member's Prompt Agent. Each persona gets its embedded system prompt
    /// and the Foundry IQ knowledge-base tool (when a KB connection is configured). Failures are
    /// isolated per agent.
    /// </summary>
    public async Task ProvisionAllAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting agent provisioning for {AgentCount} council members", CouncilMembers.All.Count);

        // Resolve the Foundry IQ knowledge-base MCP connection once (graceful degradation if absent).
        _knowledgeBaseConnectionName = ResolveKnowledgeBaseConnectionName();
        _searchEndpoint = Environment.GetEnvironmentVariable("SEARCH_SERVICE_ENDPOINT")?.Trim()?.TrimEnd('/');
        _webIqConnectionName = Environment.GetEnvironmentVariable("WEBIQ_CONNECTION_NAME")?.Trim();
        _webIqMcpUrl = Environment.GetEnvironmentVariable("WEBIQ_MCP_URL")?.Trim()?.TrimEnd('/');
        _raiPolicyName = Environment.GetEnvironmentVariable("COUNCIL_RAI_POLICY_NAME")?.Trim();

        _logger.LogInformation("Grounding provider: {Provider}", Grounding.Active);
        if (Grounding.Active == Grounding.Provider.WebIq && (string.IsNullOrEmpty(_webIqConnectionName) || string.IsNullOrEmpty(_webIqMcpUrl)))
            _logger.LogWarning("Web IQ selected but WEBIQ_CONNECTION_NAME/WEBIQ_MCP_URL not set — agents provisioned WITHOUT a grounding tool. Set them (azd output) or switch COUNCIL_GROUNDING_PROVIDER=foundryiq.");

        await Parallel.ForEachAsync(CouncilMembers.All, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (member, token) =>
        {
            var systemPrompt = CouncilPrompts.Load(member);
            var agentName = $"gc-{member.Id}";

            try
            {
                await RetryAsync(async () =>
                {
                    await CreateAgentViaRestAsync(agentName, systemPrompt, member, token);
                }, agentName, token);

                _logger.LogInformation("Provisioned agent {AgentName} with model {Model}", agentName, member.ModelDeployment);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                _logger.LogError(ex, "Provisioning failed for agent {AgentName}; continuing with the other agents", agentName);
            }
        });
    }

    private async Task RetryAsync(Func<Task> action, string operationName, CancellationToken ct, int maxRetries = 3)
    {
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                await action();
                if (attempt > 0)
                    _logger.LogInformation("Agent provisioning succeeded for {Name} on attempt {Attempt}", operationName, attempt + 1);
                return;
            }
            catch (Exception ex) when (attempt < maxRetries && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)); // 2s, 4s, 8s
                _logger.LogWarning(ex, "Agent provisioning failed for {Name}, attempt {Attempt}/{Max}. Retrying in {Delay}s...",
                    operationName, attempt + 1, maxRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent provisioning FAILED for {Name} after {Max} retries. Giving up.", operationName, maxRetries);
                throw;
            }
        }
    }

    private async Task CreateAgentViaRestAsync(
        string agentName, string systemPrompt, CouncilMember member,
        CancellationToken ct)
    {
        if (_httpClient is null || _credential is null || _projectEndpoint is null) return;

        var tokenRequest = new Azure.Core.TokenRequestContext(["https://ai.azure.com/.default"]);
        var token = await _credential.GetTokenAsync(tokenRequest, ct);

        var tools = new List<object>();

        // Web grounding: bind one MCP tool per member, chosen by the active provider. The moderator is
        // routing-only, so it never gets a grounding tool. Sources are always restricted to the member's
        // domains (Chair = all) — never free/open web search.
        var instructions = systemPrompt;
        var domains = CouncilMembers.DomainsFor(member);
        var isModerator = member.Id == "moderator";
        var hasDomains = domains is { Count: > 0 };

        var webIqAttached = !isModerator && hasDomains
            && Grounding.Active == Grounding.Provider.WebIq
            && !string.IsNullOrEmpty(_webIqConnectionName)
            && !string.IsNullOrEmpty(_webIqMcpUrl);
        var kbAttached = !isModerator && hasDomains
            && Grounding.Active == Grounding.Provider.FoundryIq
            && !string.IsNullOrEmpty(_knowledgeBaseConnectionName)
            && !string.IsNullOrEmpty(_searchEndpoint);

        if (webIqAttached)
        {
            tools.Add(new
            {
                type = "mcp",
                server_label = "webiq",
                server_url = _webIqMcpUrl,
                require_approval = "never",
                allowed_tools = new[] { "web" },
                project_connection_id = _webIqConnectionName
            });

            var sites = domains!;
            var siteOps = string.Join(" OR ", sites.Select(d => $"site:{d}"));
            instructions += $"""


                ---

                ## Web IQ grounding (restricted sources)
                Query Microsoft Web IQ (the `web` tool) for current, authoritative facts. You MUST scope
                every query to these authoritative sources using site: operators — append `{siteOps}` —
                and set `includeDomains` to: {string.Join(", ", sites)}. Do NOT perform open/free web
                search. Keep payloads small to stay within token limits: call `web` with
                `contentFormat: "passage"`, `maxResults: 3`, and `maxLength: 2000`. Make at most two
                `web` calls. Always cite the URLs returned.
                """;
        }
        else if (kbAttached)
        {
            var serverUrl = $"{_searchEndpoint}/knowledgebases/{_knowledgeBaseConnectionName}/mcp?api-version={KbMcpApiVersion}";
            tools.Add(new
            {
                type = "mcp",
                server_label = "knowledge-base",
                server_url = serverUrl,
                require_approval = "never",
                allowed_tools = new[] { "knowledge_base_retrieve" },
                project_connection_id = _knowledgeBaseConnectionName
            });

            var siteHints = string.Join(", ", domains!);
            instructions += $"""


                ---

                ## Foundry IQ knowledge base
                Query the Foundry IQ knowledge base (the knowledge-base MCP tool) for current,
                authoritative facts. It is grounded on the configured authoritative sources — prefer
                these for your domain: {siteHints}. Always cite the references it returns.
                """;
        }

        var definition = new Dictionary<string, object?>
        {
            ["kind"] = "prompt",
            ["model"] = member.ModelDeployment,
            ["instructions"] = instructions,
            ["tools"] = tools
        };
        // Per-tier reasoning effort (minimal for routing, low for personas, medium for synthesis).
        // Prompt agents run on the Responses API, so reasoning is the object { effort }, not the flat
        // Chat-Completions `reasoning_effort`. Use the Responses-surface capability check: xAI Grok
        // rejects the `reasoning` object here (it only honours reasoning_effort on Chat Completions /
        // local MAF), so it is intentionally excluded — attaching it would 400 the agent version create.
        if (CouncilModels.ReasoningEffortFor(member.Tier) is { } effort && CouncilModels.SupportsResponsesReasoningEffort(member.ModelDeployment))
            definition["reasoning"] = new { effort = CouncilModels.NormalizeEffort(member.ModelDeployment, effort) };

        // Content safety (RAI): the custom policy in COUNCIL_RAI_POLICY_NAME is already ENFORCED on this
        // agent via the model deployment — every chat deployment is bound to it in
        // infra/model-deployment.bicep (raiPolicyName), and this agent runs on member.ModelDeployment.
        // The 2025-11-15-preview PromptAgentDefinition exposes no documented agent-level RAI / content
        // filter field (definition = { kind, model, instructions, tools, reasoning? }), so we do NOT add
        // a speculative property here — an unknown field would be rejected and break provisioning.
        // TODO: when the agents API documents an agent-level RAI field (e.g. rai_policy_name /
        // content_filter), attach _raiPolicyName here. The env var is already wired for that day.
        if (!string.IsNullOrEmpty(_raiPolicyName))
            _logger.LogDebug("Agent {Name}: content policy '{Policy}' enforced via the model deployment binding.", agentName, _raiPolicyName);

        var body = JsonSerializer.Serialize(new { definition });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_projectEndpoint}/agents/{agentName}/versions?api-version=2025-11-15-preview");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Agent {Name} REST creation failed ({Status}): {Error}", agentName, response.StatusCode, error);
            response.EnsureSuccessStatusCode();
        }

        if (kbAttached)
            _logger.LogInformation("Agent {Name} provisioned WITH Foundry IQ knowledge base tool", agentName);
        else if (webIqAttached)
            _logger.LogInformation("Agent {Name} provisioned WITH Web IQ tool (restricted sources)", agentName);
    }

    private string? ResolveKnowledgeBaseConnectionName()
    {
        var name = Environment.GetEnvironmentVariable("KNOWLEDGE_BASE_CONNECTION_NAME");
        if (string.IsNullOrWhiteSpace(name))
        {
            _logger.LogInformation("Foundry IQ knowledge base not configured (no KNOWLEDGE_BASE_CONNECTION_NAME) — provisioning without the KB tool");
            return null;
        }

        _logger.LogInformation("Foundry IQ knowledge base: using connection {Name}", name);
        return name.Trim();
    }
}
