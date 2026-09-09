namespace GovernanceCouncil.Web.Services;

using GovernanceCouncil.Core.Interfaces;
using GovernanceCouncil.Core.Models;
using Microsoft.AspNetCore.SignalR;
using GovernanceCouncil.Web.Hubs;

/// <summary>
/// Service that pushes live deliberation events to connected web clients via SignalR.
/// Called by the CouncilOrchestrator as the deliberation progresses.
/// </summary>
public sealed class DeliberationNotifier : IDeliberationNotifier
{
    private readonly IHubContext<DeliberationHub> _hub;

    public DeliberationNotifier(IHubContext<DeliberationHub> hub) => _hub = hub;

    /// <summary>
    /// Notifies clients that a deliberation has started.
    /// </summary>
    public async Task DeliberationStartedAsync(string deliberationId)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("DeliberationStarted", deliberationId);
    }

    /// <summary>
    /// Notifies clients that the deliberation has completed.
    /// </summary>
    public async Task DeliberationCompleteAsync(string deliberationId, string assessmentId)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("DeliberationComplete", assessmentId);
    }

    /// <summary>
    /// Notifies clients of an error during deliberation.
    /// </summary>
    public async Task DeliberationErrorAsync(string deliberationId, string error)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("DeliberationError", error);
    }

    /// <summary>
    /// Notifies clients that an agent has started speaking (Agent Framework mode only).
    /// </summary>
    public async Task AgentSpeakingAsync(string deliberationId, string agentName)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("AgentSpeaking", agentName);
    }

    /// <summary>
    /// Pushes a streaming text chunk from an agent to clients (Agent Framework mode only).
    /// </summary>
    public async Task AgentResponseChunkAsync(string deliberationId, string agentName, string text)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("AgentResponseChunk", agentName, text);
    }

    /// <summary>
    /// Notifies clients that an agent has finished responding (Agent Framework mode only).
    /// </summary>
    public async Task AgentCompleteAsync(string deliberationId, string agentName)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("AgentComplete", agentName);
    }

    /// <summary>Announces a new debate phase (Independent Assessment / Debate / Synthesis).</summary>
    public async Task DebatePhaseAsync(string deliberationId, string phase)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("DebatePhase", phase);
    }

    /// <summary>A new debate round has started.</summary>
    public async Task DebateRoundStartedAsync(string deliberationId, int round, int maxRounds)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("DebateRoundStarted", round, maxRounds);
    }

    /// <summary>A council member raised (or lowered) their hand to speak.</summary>
    public async Task AgentHandRaisedAsync(string deliberationId, string agentName, bool wantsToSpeak, string reason)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("AgentHandRaised", agentName, wantsToSpeak, reason);
    }

    /// <summary>The Moderator opens the floor and calls for hands this round.</summary>
    public async Task ModeratorCallingForSpeakersAsync(string deliberationId, int round)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("ModeratorCallingForSpeakers", round);
    }

    /// <summary>A single member tabled its initial position (live, as its assessment lands).</summary>
    public async Task MemberAssessedAsync(string deliberationId, MemberPosition position)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("MemberAssessed", position);
    }

    /// <summary>The Moderator confirms the Independent Assessment roll-call (one sentence per member).</summary>
    public async Task ModeratorAssessmentRollCallAsync(string deliberationId, IReadOnlyList<MemberPosition> positions)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("ModeratorAssessmentRollCall", positions);
    }

    /// <summary>The Moderator selected the next speaker (push-to-talk).</summary>
    public async Task ModeratorSelectedSpeakerAsync(string deliberationId, string agentName, string reason)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("ModeratorSelectedSpeaker", agentName, reason);
    }

    /// <summary>Web-grounding citations gathered from an agent's turn.</summary>
    public async Task AgentCitationsAsync(string deliberationId, string agentName, IReadOnlyList<Citation> citations)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("AgentCitations", agentName, citations);
    }

    /// <summary>The debate has concluded.</summary>
    public async Task DebateConcludedAsync(string deliberationId, string reason)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("DebateConcluded", reason);
    }

    /// <summary>The Nexus Analyst stage (null count ⇒ running; >= 0 ⇒ finished with that many connections).</summary>
    public async Task NexusAnalysisAsync(string deliberationId, string agentName, int? connectionCount)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("NexusAnalysis", agentName, connectionCount);
    }

    /// <summary>A member's contribution was blocked by the content-safety (RAI) policy.</summary>
    public async Task ContentSafetyTriggeredAsync(string deliberationId, string agentName, string scope)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("ContentSafetyTriggered", agentName, scope);
    }

    /// <summary>The content-safety policy is being updated for this deliberation (true=applying, false=done).</summary>
    public async Task ContentSafetyApplyingAsync(string deliberationId, bool applying)
    {
        await _hub.Clients.Group(deliberationId).SendAsync("ContentSafetyApplying", applying);
    }
}
