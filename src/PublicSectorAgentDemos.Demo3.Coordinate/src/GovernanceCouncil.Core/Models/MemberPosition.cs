namespace GovernanceCouncil.Core.Models;

/// <summary>
/// A council member's initial position as confirmed by the Moderator's roll-call at the end of
/// the Independent Assessment phase: the member id (e.g. <c>gc-ciso</c>) and a one-sentence summary.
/// </summary>
public record MemberPosition(string AgentName, string Summary);
