namespace GovernanceCouncil.Core.Models;

/// <summary>
/// Which agent runtime the council uses. A single runtime toggle picks it for the whole council:
///   Foundry — Foundry Prompt Agents (provisioned in the project; default).
///   Maf     — local Microsoft Agent Framework agents on the Foundry chat models (no provisioning).
/// Default is from <c>COUNCIL_AGENT_RUNTIME</c> (else Foundry).
/// </summary>
public static class AgentRuntime
{
    public enum Mode { Foundry, Maf }

    private static volatile Mode _mode = Resolve();

    public static Mode Active => _mode;

    /// <summary>Switches the runtime in memory. Apply it via CouncilOrchestrator.ApplySettingsAsync.</summary>
    public static void SetMode(Mode mode) => _mode = mode;

    public static IReadOnlyList<Mode> AllModes { get; } = Enum.GetValues<Mode>();

    public static string DisplayName(Mode mode) => mode switch
    {
        Mode.Foundry => "Foundry Agents",
        Mode.Maf => "Local MAF Agents",
        _ => mode.ToString()
    };

    private static Mode Resolve()
    {
        var raw = Environment.GetEnvironmentVariable("COUNCIL_AGENT_RUNTIME")?.Trim();
        return raw?.ToLowerInvariant() switch
        {
            "maf" or "local" => Mode.Maf,
            _ => Mode.Foundry
        };
    }
}
