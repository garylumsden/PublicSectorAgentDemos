namespace GovernanceCouncil.Core.Models;

using System.Text.Json.Serialization;

/// <summary>
/// The scenario configuration that turns this framework into a concrete demo: branding, the
/// authoritative grounding domains, and the council composition (chair, moderator, nexus analyst +
/// debating members). Loaded once at startup from <c>config/scenario.json</c> by <see cref="Scenario"/>;
/// absent ⇒ neutral defaults. The Scenario Architect Copilot agent generates this file.
/// </summary>
public sealed record ScenarioConfig
{
    [JsonPropertyName("branding")]
    public BrandingConfig Branding { get; init; } = new();

    /// <summary>Default authoritative source domains for grounding (members may narrow further). Empty ⇒ ungrounded.</summary>
    [JsonPropertyName("groundingDomains")]
    public IReadOnlyList<string> GroundingDomains { get; init; } = [];

    [JsonPropertyName("council")]
    public CouncilConfig Council { get; init; } = new();
}

/// <summary>User-facing identity. Emblem is an emoji (e.g. "⚖️") or a path under <c>wwwroot/branding/</c> (e.g. "branding/logo.svg").</summary>
public sealed record BrandingConfig
{
    [JsonPropertyName("organisation")]
    public string Organisation { get; init; } = "";

    [JsonPropertyName("appName")]
    public string AppName { get; init; } = "Agent Council";

    [JsonPropertyName("tagline")]
    public string Tagline { get; init; } = "Multi-agent deliberation — grounded, structured, explainable.";

    [JsonPropertyName("emblem")]
    public string Emblem { get; init; } = "⚖️";
}

/// <summary>The council roster. Chair / Moderator / Nexus Analyst are framework roles; Members are the scenario personas.</summary>
public sealed record CouncilConfig
{
    [JsonPropertyName("chair")]
    public PersonaConfig Chair { get; init; } = new()
    {
        Id = "chair", Name = "Council Chair", Role = "Assessment Synthesiser",
        Description = "Synthesises member opinions into a single structured assessment.", Tier = "Synthesis"
    };

    [JsonPropertyName("moderator")]
    public PersonaConfig Moderator { get; init; } = new()
    {
        Id = "moderator", Name = "Council Moderator", Role = "Speaker Selection",
        Description = "Decides which member speaks next. Routing only — never produces content.", Tier = "Fast"
    };

    [JsonPropertyName("nexusAnalyst")]
    public PersonaConfig NexusAnalyst { get; init; } = new()
    {
        Id = "nexus-analyst", Name = "Nexus Analyst", Role = "Post-Deliberation Interconnection Discovery",
        Description = "Discovers interconnections between assessments.", Tier = "Synthesis"
    };

    /// <summary>The debating personas. Empty in an unconfigured template (configure via the Scenario Architect agent).</summary>
    [JsonPropertyName("members")]
    public IReadOnlyList<PersonaConfig> Members { get; init; } = [];
}

/// <summary>A single council persona. <see cref="PromptFile"/> is a file name under the scenario's <c>prompts/</c> directory.</summary>
public sealed record PersonaConfig
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("role")]
    public string Role { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    /// <summary>Model tier: <c>Reasoning</c> (personas), <c>Synthesis</c> (chair/nexus), or <c>Fast</c> (moderator/bids).</summary>
    [JsonPropertyName("tier")]
    public string Tier { get; init; } = "Reasoning";

    /// <summary>Authoritative source domains this persona may cite. Null/empty ⇒ inherit the scenario default.</summary>
    [JsonPropertyName("knowledgeDomains")]
    public IReadOnlyList<string>? KnowledgeDomains { get; init; }

    [JsonPropertyName("promptFile")]
    public string PromptFile { get; init; } = "";

    /// <summary>
    /// Optional avatar image shown in the debate chamber in place of the persona's initials. A path
    /// served from <c>wwwroot/</c> (e.g. <c>branding/avatars/my-chair.svg</c>) or an absolute URL.
    /// When empty, a built-in role default is used (chair / moderator / nexus-analyst / member).
    /// </summary>
    [JsonPropertyName("avatar")]
    public string? Avatar { get; init; }
}
