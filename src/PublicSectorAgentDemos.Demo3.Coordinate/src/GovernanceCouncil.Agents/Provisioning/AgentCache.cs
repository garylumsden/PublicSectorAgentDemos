namespace GovernanceCouncil.Agents.Provisioning;

using Azure.AI.Projects;
using GovernanceCouncil.Core.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry;
using Microsoft.Extensions.Logging;

/// <summary>
/// Pre-fetches and caches the Foundry agent instances used by the live debate engine.
/// UNIFIED MODEL: one tooled agent per member (<c>gc-{id}</c>) — the obsolete tool-free
/// <c>-af</c> duplicates are gone. The tooled agents (grounding tool) are invoked as black
/// boxes (final text + citations only), so tool messages never leak across the council (MAF
/// GA strips non-portable artifacts; we also build a clean text-only transcript). The roster
/// is taken from the active scenario (<see cref="CouncilMembers.DeliberationMembers"/>).
/// Retrieval uses <c>AIProjectClient.AsAIAgent(AgentReference)</c> from Microsoft.Agents.AI.Foundry.
/// </summary>
public sealed class AgentCache
{
    private const string ChairId = "chair";
    private const string ModeratorId = "moderator";

    /// <summary>Clean member agent names (e.g. <c>gc-cdio</c>) — no <c>-af</c> suffix. Index-aligned with <see cref="MemberAgents"/>.</summary>
    public string[] MemberNames { get; private set; } = [];

    public List<AIAgent> MemberAgents { get; private set; } = [];
    public AIAgent Chair { get; private set; } = null!;
    public AIAgent Moderator { get; private set; } = null!;
    public bool IsLoaded { get; private set; }

    /// <summary>
    /// Fetches the tooled member agents, chair, and moderator from Foundry. Call once at startup.
    /// Safe to call again as a fallback if startup load failed.
    /// </summary>
    public async Task LoadAsync(AIProjectClient projectClient, ILogger logger, CancellationToken ct = default)
    {
        if (IsLoaded) return;

        MemberNames = CouncilMembers.DeliberationMembers.Select(m => $"gc-{m.Id}").ToArray();
        logger.LogInformation("AgentCache: fetching {Count} unified (tooled) agents from Foundry via AsAIAgent...",
            MemberNames.Length + 2);

        var memberTasks = MemberNames.Select(name => FetchAsync(projectClient, name, ct)).ToArray();
        var chairTask = FetchAsync(projectClient, $"gc-{ChairId}", ct);
        var moderatorTask = FetchAsync(projectClient, $"gc-{ModeratorId}", ct);

        await Task.WhenAll([.. memberTasks, chairTask, moderatorTask]);

        MemberAgents = memberTasks.Select(t => t.Result).ToList();
        Chair = chairTask.Result;
        Moderator = moderatorTask.Result;
        IsLoaded = true;

        logger.LogInformation("AgentCache: {Count} agents cached successfully", MemberAgents.Count + 2);
    }

    /// <summary>Forces a re-fetch of every agent — use after re-provisioning new agent versions.</summary>
    public async Task RefreshAsync(AIProjectClient projectClient, ILogger logger, CancellationToken ct = default)
    {
        IsLoaded = false;
        await LoadAsync(projectClient, logger, ct);
    }

    /// <summary>
    /// Wraps an existing provisioned Foundry agent as a MAF <see cref="AIAgent"/> using the
    /// <c>AsAIAgent(ProjectsAgentRecord)</c> overload (binds the latest version) — no version
    /// plumbing required.
    /// </summary>
    private static Task<AIAgent> FetchAsync(AIProjectClient projectClient, string agentName, CancellationToken ct) =>
        Task.Run(() =>
        {
            var record = projectClient.AgentAdministrationClient.GetAgent(agentName, ct).Value;
            return (AIAgent)projectClient.AsAIAgent(record);
        }, ct);
}
