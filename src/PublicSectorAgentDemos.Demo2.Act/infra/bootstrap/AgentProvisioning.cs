using System.ClientModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Core;
using Defra.AgentCore;
using OpenAI.Responses;

namespace Defra.Bootstrap;

public sealed record McpAgentToolSpecification(
    string ServerLabel,
    Uri Endpoint,
    string ProjectConnectionId,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> AlwaysRequireApproval);

public sealed record AgentSpecification(
    string Name,
    string DisplayName,
    string Demo,
    string Model,
    string Instructions,
    McpAgentToolSpecification? Mcp,
    string ReasoningEffort = "low");

public sealed record AgentVersionState(string Name, string Version, string? DefinitionHash);

public sealed record AgentProvisioningResult(
    string Name,
    string DefinitionHash,
    string Action,
    string? Version);

public static class AgentDefinitionHasher
{
    public static string Compute(AgentSpecification specification)
    {
        var canonical = new
        {
            specification.Name,
            specification.DisplayName,
            specification.Demo,
            specification.Model,
            specification.ReasoningEffort,
            Instructions = specification.Instructions.ReplaceLineEndings("\n"),
            Mcp = specification.Mcp is null
                ? null
                : new
                {
                    specification.Mcp.ServerLabel,
                    Endpoint = specification.Mcp.Endpoint.AbsoluteUri,
                    specification.Mcp.ProjectConnectionId,
                    AllowedTools = specification.Mcp.AllowedTools.Order(StringComparer.Ordinal),
                    ApprovalTools = specification.Mcp.AlwaysRequireApproval.Order(StringComparer.Ordinal)
                }
        };

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }
}

public interface IAgentVersionStore
{
    Task<AgentVersionState?> GetLatestAsync(string agentName, CancellationToken cancellationToken);

    Task<string> CreateAsync(
        AgentSpecification specification,
        string definitionHash,
        CancellationToken cancellationToken);
}

public sealed class AgentProvisioner(IAgentVersionStore store)
{
    public async Task<AgentProvisioningResult> EnsureAsync(
        AgentSpecification specification,
        CancellationToken cancellationToken)
    {
        string hash = AgentDefinitionHasher.Compute(specification);
        AgentVersionState? existing = await store.GetLatestAsync(specification.Name, cancellationToken);
        if (existing is not null &&
            string.Equals(existing.DefinitionHash, hash, StringComparison.Ordinal))
        {
            return new(specification.Name, hash, "unchanged", existing.Version);
        }

        string version = await store.CreateAsync(specification, hash, cancellationToken);
        return new(specification.Name, hash, "created", version);
    }
}

public sealed class FoundryAgentVersionStore : IAgentVersionStore
{
    private const string DefinitionHashKey = "definitionHash";
    private readonly AgentAdministrationClient _client;

    public FoundryAgentVersionStore(
        Uri projectEndpoint,
        TokenCredential credential)
    {
        AIProjectClient project = new(projectEndpoint, credential);
        _client = project.AgentAdministrationClient;
    }

    public async Task<AgentVersionState?> GetLatestAsync(
        string agentName,
        CancellationToken cancellationToken)
    {
        ProjectsAgentVersion? latest = null;
        try
        {
            await foreach (ProjectsAgentVersion version in _client.GetAgentVersionsAsync(
                               agentName,
                               limit: 100,
                               order: null,
                               after: null,
                               before: null,
                               cancellationToken))
            {
                if (latest is null ||
                    int.TryParse(version.Version, out int current) &&
                    (!int.TryParse(latest.Version, out int previous) || current > previous))
                {
                    latest = version;
                }
            }
        }
        catch (ClientResultException exception) when (exception.Status == 404)
        {
            return null;
        }

        if (latest is null)
        {
            return null;
        }

        latest.Metadata.TryGetValue(DefinitionHashKey, out string? hash);
        return new(latest.Name, latest.Version, hash);
    }

    public async Task<string> CreateAsync(
        AgentSpecification specification,
        string definitionHash,
        CancellationToken cancellationToken)
    {
        DeclarativeAgentDefinition definition = new(specification.Model)
        {
            Instructions = specification.Instructions,
            ReasoningOptions = AgentReasoningOptions.CreateResponseReasoningOptions(
                specification.ReasoningEffort)
        };

        if (specification.Mcp is not null)
        {
            definition.Tools.Add(CreateMcpTool(specification.Mcp));
        }

        ProjectsAgentVersionCreationOptions options = new(definition)
        {
            Description = specification.DisplayName
        };
        options.Metadata[DefinitionHashKey] = definitionHash;
        options.Metadata["demo"] = specification.Demo;
        options.Metadata["managedBy"] = "defra-bootstrap";

        ProjectsAgentVersion created = (await _client.CreateAgentVersionAsync(
            specification.Name,
            options,
            cancellationToken: cancellationToken)).Value;
        return created.Version;
    }

    private static McpTool CreateMcpTool(McpAgentToolSpecification specification)
    {
        McpToolFilter allowed = new();
        foreach (string tool in specification.AllowedTools)
        {
            allowed.ToolNames.Add(tool);
        }

        McpToolCallApprovalPolicy approval;
        if (specification.AlwaysRequireApproval.Count == 0)
        {
            approval = new(GlobalMcpToolCallApprovalPolicy.NeverRequireApproval);
        }
        else
        {
            CustomMcpToolCallApprovalPolicy custom = new()
            {
                ToolsAlwaysRequiringApproval = new McpToolFilter(),
                ToolsNeverRequiringApproval = new McpToolFilter()
            };
            foreach (string tool in specification.AlwaysRequireApproval)
            {
                custom.ToolsAlwaysRequiringApproval.ToolNames.Add(tool);
            }

            foreach (string tool in specification.AllowedTools.Except(
                         specification.AlwaysRequireApproval,
                         StringComparer.Ordinal))
            {
                custom.ToolsNeverRequiringApproval.ToolNames.Add(tool);
            }

            approval = new(custom);
        }

        McpTool mcpTool = ResponseTool.CreateMcpTool(
            specification.ServerLabel,
            specification.Endpoint,
            authorizationToken: null,
            serverDescription: null,
            headers: null,
            allowedTools: allowed,
            toolCallApprovalPolicy: approval);
        mcpTool.ProjectConnectionId = specification.ProjectConnectionId;
        return mcpTool;
    }
}
