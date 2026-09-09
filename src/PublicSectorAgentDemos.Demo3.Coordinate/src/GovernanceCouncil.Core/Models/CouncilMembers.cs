namespace GovernanceCouncil.Core.Models;

/// <summary>
/// The active council roster, projected from the loaded <see cref="Scenario"/>. Chair / Moderator /
/// Nexus Analyst are framework roles; <see cref="DeliberationMembers"/> are the scenario personas
/// (empty until the template is configured). The public surface is unchanged from the original
/// hard-coded roster, so the debate engine is agnostic to where the personas come from.
/// </summary>
public static class CouncilMembers
{
    public static CouncilMember Moderator => ToMember(Scenario.Current.Council.Moderator);
    public static CouncilMember Chair => ToMember(Scenario.Current.Council.Chair);
    public static CouncilMember NexusAnalyst => ToMember(Scenario.Current.Council.NexusAnalyst);

    /// <summary>The debating personas for this scenario (empty in an unconfigured template).</summary>
    public static IReadOnlyList<CouncilMember> DeliberationMembers =>
        Scenario.Current.Council.Members.Select(ToMember).ToList();

    /// <summary>Every provisioned agent: Moderator, Chair, the debating members, then the Nexus Analyst.</summary>
    public static IReadOnlyList<CouncilMember> All =>
        [Moderator, Chair, .. DeliberationMembers, NexusAnalyst];

    /// <summary>
    /// The union of every deliberation member's authoritative domains plus the scenario default. The
    /// Chair synthesises all opinions, so it grounds on this full set; members stay scoped to their own.
    /// Web IQ / Foundry IQ use this as the includeDomains allowlist (never free web). Empty ⇒ ungrounded.
    /// </summary>
    public static IReadOnlyList<string> AllKnowledgeDomains =>
        Scenario.Current.GroundingDomains
            .Concat(Scenario.Current.Council.Members.SelectMany(m => m.KnowledgeDomains ?? []))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Effective source domains for a member: the Chair gets them all; others get their own, falling
    /// back to the scenario default. Null/empty ⇒ the member runs ungrounded.
    /// </summary>
    public static IReadOnlyList<string>? DomainsFor(CouncilMember member)
    {
        if (member.Id == "chair") return AllKnowledgeDomains;
        return member.KnowledgeDomains is { Count: > 0 } d ? d : Scenario.Current.GroundingDomains;
    }

    /// <summary>Resolves a council member from its agent name (e.g. "gc-cdio") or bare id.</summary>
    public static CouncilMember? ByAgentName(string agentName)
    {
        var id = agentName.StartsWith("gc-", StringComparison.OrdinalIgnoreCase) ? agentName[3..] : agentName;
        return All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static CouncilMember ToMember(PersonaConfig p) => new(
        Id: p.Id,
        Name: p.Name,
        Role: p.Role,
        Description: p.Description,
        Tier: ParseTier(p.Tier),
        PromptFile: p.PromptFile,
        KnowledgeDomains: p.KnowledgeDomains,
        Avatar: p.Avatar);

    private static CouncilModels.ModelTier ParseTier(string tier) => tier.Trim().ToLowerInvariant() switch
    {
        "synthesis" => CouncilModels.ModelTier.Synthesis,
        "fast" => CouncilModels.ModelTier.Fast,
        _ => CouncilModels.ModelTier.Reasoning
    };
}
