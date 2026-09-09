namespace GovernanceCouncil.Core.Models;

/// <summary>
/// Which web-grounding provider the council members use. A single runtime toggle picks the provider
/// for every member at once (the council re-provisions, swapping each member's MCP tool):
///   WebIq    — Microsoft Web IQ (api.microsoft.ai, MCP). Default. Restricted to each member's domains.
///   FoundryIq — Foundry IQ Knowledge Base (Bing Web KS on Azure AI Search).
/// Default is from <c>COUNCIL_GROUNDING_PROVIDER</c> (else WebIq).
/// </summary>
public static class Grounding
{
    public enum Provider { WebIq, FoundryIq }

    private static volatile Provider _provider = Resolve();

    public static Provider Active => _provider;

    /// <summary>Switches the provider in memory. Apply it to agents via CouncilOrchestrator.ApplySettingsAsync.</summary>
    public static void SetProvider(Provider provider) => _provider = provider;

    public static IReadOnlyList<Provider> AllProviders { get; } = Enum.GetValues<Provider>();

    public static string DisplayName(Provider provider) => provider switch
    {
        Provider.WebIq => "Web IQ",
        Provider.FoundryIq => "Foundry IQ",
        _ => provider.ToString()
    };

    private static Provider Resolve()
    {
        var raw = Environment.GetEnvironmentVariable("COUNCIL_GROUNDING_PROVIDER")?.Trim();
        return raw?.ToLowerInvariant() switch
        {
            "foundryiq" or "foundry" => Provider.FoundryIq,
            _ => Provider.WebIq
        };
    }
}
