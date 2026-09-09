namespace GovernanceCouncil.Agents.Provisioning;

using System.Reflection;
using GovernanceCouncil.Core.Models;

/// <summary>
/// Resolves a council member's system prompt. Scenario personas come from the on-disk prompts
/// directory (<see cref="Scenario.PromptsDirectory"/>) so the Scenario Architect Copilot agent can
/// author them without a rebuild. The framework roles (chair / moderator / nexus-analyst) fall back to
/// embedded, scenario-neutral defaults; any other persona without a prompt file falls back to a
/// minimal identity prompt built from its name / role / description.
/// </summary>
internal static class CouncilPrompts
{
    public static string Load(CouncilMember member)
    {
        if (!string.IsNullOrWhiteSpace(member.PromptFile))
        {
            var path = Path.Combine(Scenario.PromptsDirectory, member.PromptFile);
            if (File.Exists(path)) return File.ReadAllText(path);
        }

        return LoadEmbeddedDefault(member.Id) ?? FallbackIdentity(member);
    }

    private static string? LoadEmbeddedDefault(string memberId)
    {
        var fileName = memberId switch
        {
            "chair" => "chair.md",
            "moderator" => "moderator.md",
            "nexus-analyst" => "nexus-analyst.md",
            _ => null
        };
        if (fileName is null) return null;

        var assembly = Assembly.GetExecutingAssembly();
        var full = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".Prompts.{fileName}", StringComparison.OrdinalIgnoreCase));
        if (full is null) return null;

        using var stream = assembly.GetManifestResourceStream(full)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string FallbackIdentity(CouncilMember m) => $"""
        You are {m.Name}, the council's {m.Role}.
        {m.Description}

        Contribute to the council's deliberation from your area of expertise. Be specific, evidence-led,
        and concise. When you state a fact, cite the source it came from.
        """;
}
