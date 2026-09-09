namespace GovernanceCouncil.Agents.Provisioning;

using Azure.AI.Projects;

/// <summary>
/// Small helpers bridging the Azure.AI.Projects 2.0.x GA client surface.
/// In the GA client the agent record (<c>ProjectsAgentRecord</c>) no longer exposes its version
/// list, so the Foundry Responses path (which needs a concrete <c>AgentReference(name, version)</c>)
/// resolves the latest version via <c>AgentAdministrationClient.GetAgentVersions</c>.
/// </summary>
internal static class FoundryAgentHelpers
{
    public static string ResolveLatestVersion(AIProjectClient client, string agentName, CancellationToken ct)
    {
        try
        {
            var versions = client.AgentAdministrationClient.GetAgentVersions(agentName, null, null, null, null, ct);
            string? best = null;
            var bestNumeric = -1;
            foreach (var v in versions)
            {
                if (int.TryParse(v.Version, out var n))
                {
                    if (n > bestNumeric) { bestNumeric = n; best = v.Version; }
                }
                else
                {
                    best ??= v.Version;
                }
            }
            return best ?? "1";
        }
        catch
        {
            return "1";
        }
    }
}
