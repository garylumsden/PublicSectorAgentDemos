namespace GovernanceCouncil.Web;

using GovernanceCouncil.Core.Models;

/// <summary>
/// Resolves the avatar image shown for a council persona in the debate chamber (in place of initials).
/// A persona's configured <c>avatar</c> in <c>scenario.json</c> wins; otherwise a built-in role default
/// is used. The four defaults ship under <c>wwwroot/branding/avatars/</c> — <c>chair</c>, <c>moderator</c>,
/// <c>nexus</c>, <c>member</c> — and any of them is overridable per persona in config.
/// </summary>
public static class Avatars
{
    private const string DefaultsBase = "branding/avatars/";

    /// <summary>The avatar URL for a member: its configured override, else the role default.</summary>
    public static string For(CouncilMember member) =>
        string.IsNullOrWhiteSpace(member.Avatar)
            ? $"{DefaultsBase}{RoleDefault(member.Id)}.svg"
            : member.Avatar!.Trim();

    /// <summary>The avatar URL for an agent key (e.g. "gc-chair"); falls back to the member default.</summary>
    public static string For(string agentKey) =>
        CouncilMembers.ByAgentName(agentKey) is { } m ? For(m) : $"{DefaultsBase}member.svg";

    private static string RoleDefault(string id) => id.Trim().ToLowerInvariant() switch
    {
        "chair" => "chair",
        "moderator" => "moderator",
        "nexus-analyst" => "nexus",
        _ => "member"
    };
}
