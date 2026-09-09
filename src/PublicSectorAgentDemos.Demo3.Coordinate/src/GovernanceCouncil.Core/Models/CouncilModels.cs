namespace GovernanceCouncil.Core.Models;

/// <summary>
/// Central, config-driven model selection. A single <c>COUNCIL_MODEL_PROFILE</c> toggle picks a
/// named model set across every tier at once; individual <c>COUNCIL_*_MODEL</c> env vars still
/// override single cells if set. The matching deployments must exist in the Foundry project
/// (see infra/ Bicep + quota). Changing the profile requires an app restart (agents re-provision
/// with the new model).
///
/// Profiles (default <see cref="ModelProfile.Fast"/>):
///   Frontier — highest quality: gpt-5.4 personas + chair (slowest, priciest)
///   Balanced — gpt-5-mini personas with a premium gpt-5.4 chair synthesis
///   Fast     — all gpt-5-mini personas + chair (schema-reliable), gpt-5-nano routing
/// </summary>
public static class CouncilModels
{
    public enum ModelProfile { Frontier, Balanced, Fast }

    /// <summary>The model tier each council role maps to.</summary>
    public enum ModelTier { Reasoning, Synthesis, Fast }

    // Mutable so the profile can be switched at runtime (the council re-provisions on the new set).
    private static volatile ModelProfile _profile = ResolveProfile();

    /// <summary>The active model profile (default from <c>COUNCIL_MODEL_PROFILE</c>, else Fast).</summary>
    public static ModelProfile Profile => _profile;

    /// <summary>Switches the active profile in memory. Apply it to the agents via CouncilOrchestrator.ApplySettingsAsync.</summary>
    public static void SetProfile(ModelProfile profile) => _profile = profile;

    /// <summary>All selectable profiles, for the UI toggle.</summary>
    public static IReadOnlyList<ModelProfile> AllProfiles { get; } = Enum.GetValues<ModelProfile>();

    /// <summary>Human-friendly name for any profile (each enum name is already display-ready).</summary>
    public static string DisplayName(ModelProfile profile) => profile.ToString();

    /// <summary>Human-friendly name of the active profile.</summary>
    public static string ProfileDisplayName => DisplayName(Profile);

    // tier -> model name (== Foundry deployment name) per profile.
    private static readonly Dictionary<ModelProfile, (string Reasoning, string Synthesis, string Fast)> Sets = new()
    {
        [ModelProfile.Frontier] = ("gpt-5.4",                 "gpt-5.4",                 "gpt-5-mini"),
        [ModelProfile.Balanced] = ("gpt-5-mini",              "gpt-5.4",                 "gpt-5-nano"),
        [ModelProfile.Fast]     = ("gpt-5-mini",              "gpt-5-mini",              "gpt-5-nano"),
    };

    private static (string Reasoning, string Synthesis, string Fast) Set => Sets[_profile];

    /// <summary>Deep multi-turn argumentation for the debating personas (also their bids).</summary>
    public static string Reasoning => Resolve("COUNCIL_REASONING_MODEL", Set.Reasoning);

    /// <summary>Final synthesis and nexus discovery — accuracy and structured output matter most.</summary>
    public static string Synthesis => Resolve("COUNCIL_SYNTHESIS_MODEL", Set.Synthesis);

    /// <summary>Fast, cheap routing/tallying for the moderator — latency over depth.</summary>
    public static string Fast => Resolve("COUNCIL_FAST_MODEL", Set.Fast);

    /// <summary>
    /// Bid + speaker-selection model for the local MAF runtime: the fastest model in the active
    /// profile. Defaults to the profile's <see cref="Fast"/> tier and runs at the Fast tier's effort —
    /// <c>minimal</c> for the GPT-5 family — so bids stay cheap and low-latency. Override with
    /// <c>COUNCIL_BID_MODEL</c>.
    /// </summary>
    public static string BidModel => Resolve("COUNCIL_BID_MODEL", Fast);

    /// <summary>The current model for a given role tier (honours per-tier env overrides).</summary>
    public static string ForTier(ModelTier tier) => tier switch
    {
        ModelTier.Synthesis => Synthesis,
        ModelTier.Fast => Fast,
        _ => Reasoning
    };

    /// <summary>
    /// Whether to enforce strict <c>json_schema</c> Structured Outputs. The GPT-5 family supports it.
    /// Override via <c>COUNCIL_STRUCTURED_OUTPUTS</c>; defaults on for every profile.
    /// </summary>
    public static bool UseStructuredOutputs =>
        ResolveBool("COUNCIL_STRUCTURED_OUTPUTS", true);

    /// <summary>
    /// Max concurrent model calls (Phase-1 assessments, bid round). Capacities are 500 (≈500 RPM /
    /// 500K TPM) so a 7-way burst is safe; override with <c>COUNCIL_MAX_PARALLELISM</c>.
    /// </summary>
    public static int MaxParallelism { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("COUNCIL_MAX_PARALLELISM"), out var n) && n > 0
            ? n : 7;

    /// <summary>
    /// Lower concurrency cap used while Web IQ is the grounding provider (limited-access, low RPM):
    /// throttles Phase-1 so 7 agents don't burst the web tool into 429s. Override with
    /// <c>COUNCIL_GROUNDING_MAX_PARALLELISM</c>.
    /// </summary>
    public static int GroundingMaxParallelism { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("COUNCIL_GROUNDING_MAX_PARALLELISM"), out var g) && g > 0
            ? g : 2;

    /// <summary>
    /// Whether a deployment accepts a <c>reasoning_effort</c> setting — the GPT-5 reasoning family and
    /// the o-series. The <c>-chat</c> variants do NOT.
    /// </summary>
    public static bool SupportsReasoningEffort(string deployment)
    {
        var d = (deployment ?? "").Trim().ToLowerInvariant();
        if (d.Contains("chat")) return false;
        return d.StartsWith("gpt-5") || d.StartsWith("o1") || d.StartsWith("o3") || d.StartsWith("o4");
    }

    /// <summary>
    /// Whether a deployment accepts the reasoning-effort setting on the Foundry **Responses API / Prompt
    /// Agent** surface (the <c>definition.reasoning.effort</c> object), as opposed to Chat Completions'
    /// flat <c>reasoning_effort</c>. Use this for Foundry Prompt Agents; use
    /// <see cref="SupportsReasoningEffort"/> for in-process MAF chat clients.
    /// </summary>
    public static bool SupportsResponsesReasoningEffort(string deployment) =>
        SupportsReasoningEffort(deployment);

    /// <summary>
    /// Normalises a reasoning-effort value for a specific model. No special-casing is currently needed.
    /// </summary>
    public static string NormalizeEffort(string model, string effort) => effort;

    /// <summary>
    /// Per-tier reasoning effort (reasoning models only — see <see cref="SupportsReasoningEffort"/>).
    /// Lowest latency where speed matters, more deliberation where accuracy matters:
    ///   Fast (bids / moderator routing) → <c>minimal</c>,
    ///   Reasoning (debating personas, initial positions + speeches) → <c>low</c>,
    ///   Synthesis (Chair + Nexus) → <c>medium</c> (balanced).
    /// A non-empty <c>COUNCIL_REASONING_EFFORT</c> forces a single value across every tier; an empty
    /// value disables it entirely (model default). Non-reasoning models (<c>-chat</c>) ignore it.
    /// </summary>
    public static string? ReasoningEffortFor(ModelTier tier)
    {
        if (Environment.GetEnvironmentVariable("COUNCIL_REASONING_EFFORT") is { } v)
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        return tier switch
        {
            ModelTier.Fast => "minimal",
            ModelTier.Reasoning => "low",
            ModelTier.Synthesis => "medium",
            _ => "minimal"
        };
    }

    private static ModelProfile ResolveProfile()
    {
        var raw = Environment.GetEnvironmentVariable("COUNCIL_MODEL_PROFILE");
        if (string.IsNullOrWhiteSpace(raw)) return ModelProfile.Fast;
        var key = new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return key switch
        {
            "frontier" or "normal" => ModelProfile.Frontier,
            "balanced" => ModelProfile.Balanced,
            // "veryfast" is the legacy name for the all-mini Fast profile; legacy "fast" (mini debate +
            // premium synthesis) is now "balanced", but bare "fast" resolves to the canonical Fast set.
            "fast" or "veryfast" => ModelProfile.Fast,
            _ => ModelProfile.Fast
        };
    }

    private static string Resolve(string envVar, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static bool ResolveBool(string envVar, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return value.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";
    }
}
