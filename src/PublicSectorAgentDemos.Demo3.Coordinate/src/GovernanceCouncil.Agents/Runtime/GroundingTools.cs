namespace GovernanceCouncil.Agents.Runtime;

using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using GovernanceCouncil.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// Builds the grounding tool (an <see cref="AIFunction"/>) handed to a local MAF member agent. The
/// tool calls the SAME backends the Foundry agents use, but in-process over REST, scoped to the
/// member's restricted domains (<see cref="CouncilMembers.DomainsFor"/>) and the active provider:
///   WebIq     → Microsoft Web IQ web search (/v3/search/web), passage content, site:+includeDomains.
///   FoundryIq → the Foundry IQ knowledge base retrieve endpoint on Azure AI Search.
/// Never free/open web search — every query is hard-scoped to the member's authoritative domains.
/// </summary>
internal static class GroundingTools
{
    private const string SearchAudience = "https://search.azure.com/.default";
    private static readonly string WebIqRestUrl =
        Environment.GetEnvironmentVariable("WEBIQ_REST_URL")?.Trim() is { Length: > 0 } u
            ? u : "https://api.microsoft.ai/v3/search/web";
    private const string KbApiVersion = "2026-05-01-preview";

    /// <summary>The grounding config the MAF runtime resolves once and passes to each tool.</summary>
    public sealed record Config(
        HttpClient Http,
        TokenCredential Credential,
        string? SearchEndpoint,
        string KnowledgeBaseName,
        string? WebIqApiKey,
        ILogger Logger);

    /// <summary>
    /// A per-member, per-turn budget of grounding-tool calls. The runtime resets <see cref="Remaining"/>
    /// at the start of every turn; each tool invocation decrements it. A tool-eager model (notably Grok)
    /// is hard-stopped once the budget is exhausted, so a single turn can't fire dozens of search calls.
    /// </summary>
    public sealed class SearchBudget { public int Remaining; }

    /// <summary>
    /// Returns the grounding tool for a member, or null when grounding isn't configured for the active
    /// provider (the agent then runs ungrounded — graceful degradation, same as the Foundry path).
    /// </summary>
    public static AIFunction? ForMember(CouncilMember member, Config cfg, SearchBudget budget)
    {
        // Effective domains for this member (Chair = all). No domains ⇒ no grounding tool (ungrounded):
        // the template never performs free/open web search, so with no authoritative allowlist the
        // member simply runs without a grounding tool.
        if (CouncilMembers.DomainsFor(member) is not { Count: > 0 } domains)
            return null;

        return Grounding.Active switch
        {
            Grounding.Provider.WebIq when !string.IsNullOrEmpty(cfg.WebIqApiKey) =>
                BuildWebIqTool(domains, cfg, budget),
            Grounding.Provider.FoundryIq when !string.IsNullOrEmpty(cfg.SearchEndpoint) =>
                BuildKnowledgeBaseTool(cfg, budget),
            _ => null
        };
    }

    private static AIFunction BuildWebIqTool(IReadOnlyList<string> domains, Config cfg, SearchBudget budget)
    {
        var siteOps = string.Join(" OR ", domains.Select(d => $"site:{d}"));

        async Task<string> SearchWeb(
            [Description("What to look up in the configured authoritative sources.")] string query,
            CancellationToken ct)
        {
            if (System.Threading.Interlocked.Decrement(ref budget.Remaining) < 0)
                return "Search budget reached for this turn — do not search again; answer from the evidence already gathered.";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, WebIqRestUrl);
                req.Headers.TryAddWithoutValidation("x-apikey", cfg.WebIqApiKey);
                req.Content = JsonContent.Create(new
                {
                    query = $"{query} {siteOps}",
                    includeDomains = domains,
                    contentFormat = "passage",
                    maxResults = 3,
                    maxLength = 2000
                });
                using var resp = await cfg.Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    cfg.Logger.LogWarning("Web IQ search failed ({Status})", resp.StatusCode);
                    return "No results.";
                }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                return FormatWebResults(doc.RootElement);
            }
            catch (Exception ex)
            {
                cfg.Logger.LogWarning(ex, "Web IQ search threw");
                return "No results.";
            }
        }

        return AIFunctionFactory.Create(SearchWeb,
            name: "search_web",
            description: $"Search the configured authoritative sources ({string.Join(", ", domains)}) " +
                         "via Microsoft Web IQ. Returns ranked passages with source URLs to cite. Use only these sources.");
    }

    private static AIFunction BuildKnowledgeBaseTool(Config cfg, SearchBudget budget)
    {
        var endpoint = cfg.SearchEndpoint!.TrimEnd('/');

        async Task<string> RetrieveKnowledge(
            [Description("What to look up in the Foundry IQ knowledge base.")] string query,
            CancellationToken ct)
        {
            if (System.Threading.Interlocked.Decrement(ref budget.Remaining) < 0)
                return "Search budget reached for this turn — do not search again; answer from the evidence already gathered.";
            try
            {
                var token = await cfg.Credential.GetTokenAsync(new TokenRequestContext([SearchAudience]), ct);
                var url = $"{endpoint}/knowledgebases/{cfg.KnowledgeBaseName}/retrieve?api-version={KbApiVersion}";
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
                req.Content = JsonContent.Create(new
                {
                    messages = new[] { new { role = "user", content = new[] { new { type = "text", text = query } } } }
                });
                using var resp = await cfg.Http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    cfg.Logger.LogWarning("KB retrieve failed ({Status})", resp.StatusCode);
                    return "No results.";
                }
                return body; // synthesised answer + refs; the agent reads and cites the returned URLs.
            }
            catch (Exception ex)
            {
                cfg.Logger.LogWarning(ex, "KB retrieve threw");
                return "No results.";
            }
        }

        return AIFunctionFactory.Create(RetrieveKnowledge,
            name: "retrieve_knowledge",
            description: "Query the Foundry IQ knowledge base (configured authoritative sources). Returns a " +
                         "cited answer with reference URLs. Always cite the references it returns.");
    }

    private static string FormatWebResults(JsonElement root)
    {
        if (!root.TryGetProperty("webResults", out var results) || results.ValueKind != JsonValueKind.Array)
            return "No results.";

        var sb = new StringBuilder();
        foreach (var r in results.EnumerateArray())
        {
            var title = r.TryGetProperty("title", out var t) ? t.GetString() : null;
            var url = r.TryGetProperty("url", out var u) ? u.GetString() : null;
            var content = r.TryGetProperty("content", out var c) ? c.GetString() : null;
            sb.AppendLine($"### {title}\n{url}\n{content}\n");
        }
        return sb.Length > 0 ? sb.ToString() : "No results.";
    }
}
