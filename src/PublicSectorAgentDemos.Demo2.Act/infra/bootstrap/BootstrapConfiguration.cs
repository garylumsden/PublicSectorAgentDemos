using System.Text.Json;
using System.Text.Json.Serialization;

namespace Defra.Bootstrap;

public sealed record BootstrapConfiguration(
    Uri ProjectEndpoint,
    string TenantId,
    string ModelDeploymentName,
    string ReasoningEffort,
    string RepositoryRoot,
    string AgentCatalogPath)
{
    public static BootstrapConfiguration FromEnvironment(string repositoryRoot)
    {
        string root = Path.GetFullPath(repositoryRoot);
        string endpoint = Required("AZURE_AI_PROJECT_ENDPOINT");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? projectEndpoint) ||
            projectEndpoint.Scheme != Uri.UriSchemeHttps || projectEndpoint.Port != 443 ||
            !projectEndpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase) ||
            !projectEndpoint.AbsolutePath.StartsWith("/api/projects/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(projectEndpoint.UserInfo) || !string.IsNullOrEmpty(projectEndpoint.Query) ||
            !string.IsNullOrEmpty(projectEndpoint.Fragment))
        {
            throw new BootstrapConfigurationException("AZURE_AI_PROJECT_ENDPOINT must be a Foundry project HTTPS endpoint.");
        }

        if (!Guid.TryParse(Required("AZURE_TENANT_ID"), out Guid tenant) || tenant == Guid.Empty)
        {
            throw new BootstrapConfigurationException("AZURE_TENANT_ID must be a non-empty GUID.");
        }

        string model = Required("AZURE_AI_MODEL_DEPLOYMENT_NAME");
        if (model.Length > 128 || model.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new BootstrapConfigurationException("AZURE_AI_MODEL_DEPLOYMENT_NAME must be a safe identifier.");
        }

        string effort = Required("AZURE_AI_REASONING_EFFORT");
        if (effort is not ("low" or "medium" or "high"))
        {
            throw new BootstrapConfigurationException("AZURE_AI_REASONING_EFFORT must be low, medium, or high.");
        }

        return new(projectEndpoint, tenant.ToString("D"), model, effort, root,
            Path.Combine(root, "config", "agents", "catalog.v1.json"));
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value
            : throw new BootstrapConfigurationException($"{name} is required.");
}

public sealed class BootstrapConfigurationException(string message) : Exception(message);

public sealed record AgentCatalog(string SchemaVersion, AgentCatalogEntry[] Agents);

public sealed record AgentCatalogEntry(
    string Name,
    string DisplayName,
    string Demo,
    string InstructionsPath,
    AgentMcpConfiguration? Mcp,
    string ModelTier = "quality");

public sealed record AgentMcpConfiguration(
    string ServerLabel,
    string EndpointEnvironmentVariable,
    string ProjectConnectionId,
    string[] AllowedTools,
    string[] AlwaysRequireApproval);

public static class AgentCatalogLoader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyList<AgentSpecification> Load(
        BootstrapConfiguration configuration,
        Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        AgentCatalog catalog = JsonSerializer.Deserialize<AgentCatalog>(
                                   File.ReadAllText(configuration.AgentCatalogPath),
                                   Options)
                               ?? throw new BootstrapConfigurationException("Agent catalog is empty.");

        if (catalog.SchemaVersion != "1.0.0" ||
            catalog.Agents.Length == 0 ||
            catalog.Agents.Select(agent => agent.Name).Distinct(StringComparer.Ordinal).Count() != catalog.Agents.Length)
        {
            throw new BootstrapConfigurationException("Agent catalog identity or names are invalid.");
        }

        AgentCatalogEntry[] floodSupport = catalog.Agents.Where(entry => entry.Demo == "demo2").ToArray();
        if (catalog.Agents.Length != 1 || floodSupport.Length != 1 ||
            floodSupport[0].Name != "cross-government-flood-support" ||
            floodSupport[0].Mcp is not { } tools ||
            tools.ServerLabel != "flood-support" ||
            tools.ProjectConnectionId != "flood-support-connection" ||
            tools.EndpointEnvironmentVariable != "DEFRA_TOOLS_MCP_FLOOD_SUPPORT_URL" ||
            !tools.AllowedTools.Order(StringComparer.Ordinal).SequenceEqual(
                new[] { "checkTransportCapacity", "findAccommodation", "getSituationReports", "reserveSupportPackage" }) ||
            !tools.AlwaysRequireApproval.SequenceEqual(new[] { "reserveSupportPackage" }))
        {
            throw new BootstrapConfigurationException("The Demo2 catalog must preserve the flood support tools and reservation approval contract.");
        }

        return floodSupport.Select(entry =>
        {
            string promptPath = ResolveCatalogPath(configuration.RepositoryRoot, entry.InstructionsPath);
            string instructions = File.ReadAllText(promptPath).Trim();
            if (string.IsNullOrWhiteSpace(instructions))
            {
                throw new BootstrapConfigurationException($"Instructions for '{entry.Name}' are empty.");
            }

            McpAgentToolSpecification? mcp = null;
            if (entry.Mcp is not null)
            {
                if (!IsSafeIdentifier(entry.Mcp.ProjectConnectionId))
                {
                    throw new BootstrapConfigurationException(
                        $"Project connection ID for '{entry.Name}' is invalid.");
                }

                string endpointValue = readEnvironment(entry.Mcp.EndpointEnvironmentVariable)
                    ?? throw new BootstrapConfigurationException(
                        $"Required MCP endpoint variable '{entry.Mcp.EndpointEnvironmentVariable}' is missing.");
                if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out Uri? endpoint) ||
                    endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Port != 443 ||
                    endpoint.AbsolutePath.TrimEnd('/') != "/mcp/flood-support" ||
                    !string.IsNullOrEmpty(endpoint.UserInfo) ||
                    !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
                {
                    throw new BootstrapConfigurationException(
                        $"MCP endpoint '{entry.Mcp.EndpointEnvironmentVariable}' must be HTTPS.");
                }

                mcp = new(
                    entry.Mcp.ServerLabel,
                    endpoint,
                    entry.Mcp.ProjectConnectionId,
                    entry.Mcp.AllowedTools,
                    entry.Mcp.AlwaysRequireApproval);
            }

            string model = entry.ModelTier switch
            {
                "quality" => configuration.ModelDeploymentName,
                _ => throw new BootstrapConfigurationException(
                    $"Agent '{entry.Name}' has unsupported model tier '{entry.ModelTier}'.")
            };

            return new AgentSpecification(
                entry.Name,
                entry.DisplayName,
                entry.Demo,
                model,
                instructions,
                mcp,
                configuration.ReasoningEffort);
        }).ToArray();
    }

    private static string ResolveCatalogPath(string root, string path)
    {
        if (Path.IsPathRooted(path) ||
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "." or ".."))
        {
            throw new BootstrapConfigurationException("Agent instruction paths must be repository-relative.");
        }

        string fullPath = Path.GetFullPath(Path.Combine(root, path));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            throw new BootstrapConfigurationException($"Agent instruction path '{path}' is invalid.");
        }

        return fullPath;
    }

    private static bool IsSafeIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.');
}
