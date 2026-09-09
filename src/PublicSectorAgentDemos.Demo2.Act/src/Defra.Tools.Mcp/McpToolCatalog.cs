using Defra.Policy;

namespace Defra.Tools.Mcp;

public static class McpToolNames
{
    public const string LookupFarmRegister = "getSituationReports";
    public const string GetComplaintHistory = "findAccommodation";
    public const string GetWeatherData = "checkTransportCapacity";
    public const string DispatchVet = "reserveSupportPackage";
}

public sealed record McpEndpointGroup(
    string Name,
    string Path,
    string ServerName,
    string Instructions,
    IReadOnlySet<string> ToolNames);

public static class McpToolCatalog
{
    public const string Version = "1.0.0";

    public static IReadOnlyList<McpEndpointGroup> Groups { get; } =
    [
        new(
            "flood-support",
            "/mcp/flood-support",
            "cross-government-flood-support-mcp",
            "Read situation reports, accommodation and transport capacity. Call reserveSupportPackage for an exact versioned package; Foundry must pause for approval. Retain source conflicts and unmet household needs. This endpoint uses demonstration data.",
            new HashSet<string>(
            [
                McpToolNames.LookupFarmRegister,
                McpToolNames.GetComplaintHistory,
                McpToolNames.GetWeatherData,
                McpToolNames.DispatchVet
            ], StringComparer.Ordinal))
    ];

    public static IReadOnlySet<string> AllToolNames { get; } =
        new HashSet<string>(
            Groups.SelectMany(group => group.ToolNames),
            StringComparer.Ordinal);

    public static bool TryGetGroup(PathString path, out McpEndpointGroup? group)
    {
        group = Groups.SingleOrDefault(candidate => path.StartsWithSegments(candidate.Path));
        return group is not null;
    }
}

public static class McpServicePolicy
{
    public const string PolicyId = "defra-mcp-demo-policy";
    public const string PolicyVersion = McpToolCatalog.Version;

    public static PolicyConfiguration CreateDefault() =>
        new(
            PolicyId,
            PolicyVersion,
            McpToolCatalog.AllToolNames.Order(StringComparer.Ordinal).ToArray(),
            McpToolCatalog.AllToolNames
                .Order(StringComparer.Ordinal)
                .Select(toolName => new ToolApprovalRule(
                    toolName,
                    toolName is McpToolNames.DispatchVet))
                .ToArray(),
            new PolicyInputLimits(
                MaxInputCharacters: 196_608,
                MaxToolArguments: 7,
                MaxToolArgumentCharacters: 196_608,
                MaxToolCallsPerTurn: 16),
            [
                @"\b(ignore|disregard)\s+(all\s+)?(previous|prior|system)\s+instructions?\b",
                @"\b(override|reveal|print|show)\s+(the\s+)?(system|developer)\s+(prompt|message|instructions?)\b",
                @"\b(do\s+not|don't)\s+follow\s+(the\s+)?(rules|policy|instructions)\b",
                @"\b(begin|end)\s+(system|assistant|developer)\s+(message|prompt)\b"
            ],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = "strict",
                ["reserveSupportPackageApprovalOwner"] = "agent-toolbox-policy",
                ["legalConclusionMode"] = "disabled"
            });
}
