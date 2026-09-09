namespace GovernanceCouncil.Core.Interfaces;

using GovernanceCouncil.Core.Models;

public interface IDeliberationNotifier
{
    Task DeliberationStartedAsync(string deliberationId);
    Task DeliberationCompleteAsync(string deliberationId, string assessmentId);
    Task DeliberationErrorAsync(string deliberationId, string error);
    Task AgentSpeakingAsync(string deliberationId, string agentName);
    Task AgentResponseChunkAsync(string deliberationId, string agentName, string text);
    Task AgentCompleteAsync(string deliberationId, string agentName);

    // --- Live debate (hands-up / push-to-talk) events ---

    /// <summary>Announces a new debate phase, e.g. "Independent Assessment", "Debate", "Synthesis".</summary>
    Task DebatePhaseAsync(string deliberationId, string phase);

    /// <summary>A new debate round has started (1-based) out of a maximum.</summary>
    Task DebateRoundStartedAsync(string deliberationId, int round, int maxRounds);

    /// <summary>A council member has decided whether to raise their hand to speak this round.</summary>
    Task AgentHandRaisedAsync(string deliberationId, string agentName, bool wantsToSpeak, string reason);

    /// <summary>The Moderator opens the floor and calls for members to raise their hand this round.</summary>
    Task ModeratorCallingForSpeakersAsync(string deliberationId, int round);

    /// <summary>
    /// A single council member has tabled its initial position during the Independent Assessment
    /// phase. Emitted live, the moment that member's own assessment is parsed, so its initial-position
    /// card fills in immediately instead of waiting for the whole roll-call to complete.
    /// </summary>
    Task MemberAssessedAsync(string deliberationId, MemberPosition position);

    /// <summary>
    /// The Moderator confirms the Independent Assessment roll-call: every member has given their
    /// initial position, summarised here as one sentence each.
    /// </summary>
    Task ModeratorAssessmentRollCallAsync(string deliberationId, IReadOnlyList<MemberPosition> positions);

    /// <summary>The Moderator has selected the next speaker from those who raised their hand (push-to-talk).</summary>
    Task ModeratorSelectedSpeakerAsync(string deliberationId, string agentName, string reason);

    /// <summary>Web-grounding citations gathered from an agent's turn.</summary>
    Task AgentCitationsAsync(string deliberationId, string agentName, IReadOnlyList<Citation> citations);

    /// <summary>The debate has concluded (no further hands, or the round cap was reached).</summary>
    Task DebateConcludedAsync(string deliberationId, string reason);

    /// <summary>
    /// A member's contribution was blocked by the Azure content-safety (Responsible AI) policy — the
    /// model's input or output was rated at/above the policy's blocking threshold. Surfaced to the user
    /// so a content-safety trigger is visible rather than silently swallowed. <paramref name="scope"/>
    /// is a short label: <c>input</c> | <c>output</c> | <c>content</c>.
    /// </summary>
    Task ContentSafetyTriggeredAsync(string deliberationId, string agentName, string scope);

    /// <summary>
    /// The content-safety policy is being changed for this deliberation (control-plane update + a few
    /// seconds to propagate). <paramref name="applying"/> true while it runs, false when done — lets the
    /// UI show that the new strictness is being applied before the council convenes.
    /// </summary>
    Task ContentSafetyApplyingAsync(string deliberationId, bool applying);

    /// <summary>
    /// The post-deliberation Nexus Analyst stage. <paramref name="connectionCount"/> is null while it
    /// runs and the discovered count once finished (0 ⇒ no connections). Lets the Nexus Analyst appear
    /// in the live thread the same way the Chair's synthesis turn does.
    /// </summary>
    Task NexusAnalysisAsync(string deliberationId, string agentName, int? connectionCount);
}
