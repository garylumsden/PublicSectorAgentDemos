namespace GovernanceCouncil.Core.Models;

public record CouncilMember(
    string Id,
    string Name,
    string Role,
    string Description,
    CouncilModels.ModelTier Tier,
    string PromptFile,
    IReadOnlyList<string>? KnowledgeDomains = null,
    string? Avatar = null
)
{
    /// <summary>The model deployment for this member under the active profile (resolved live).</summary>
    public string ModelDeployment => CouncilModels.ForTier(Tier);
}
