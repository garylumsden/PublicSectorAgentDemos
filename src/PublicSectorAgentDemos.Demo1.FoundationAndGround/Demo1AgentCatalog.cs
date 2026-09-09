using System.Text.Json;
using System.Text.Json.Serialization;

namespace PublicSectorAgentDemos.Demo1.FoundationAndGround;

public static class Demo1AgentCatalog
{
    public const string FoundationAgentName = "managing-public-money-foundation";
    public const string GroundAgentName = "managing-public-money-ground";
    public const string ApprovedSourceId = "govuk-managing-public-money-2026";
    public const string ModelDeploymentName = "gpt-5-mini";
    public const string FoundationReasoningEffort = "low";
    public const string GroundReasoningEffort = "low";
    public const string GroundRetrievalReasoningEffort = "minimal";
    public const string PinnedChecksum = "d763aa52d5fcd2cf4f7b011d57d7149cfdcd82a85adb5f9414054cffa86476f5";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static Demo1AgentCatalogDocument Load(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        string path = Path.Combine(repositoryRoot, "config", "ground", "agent-definitions.v1.json");
        Demo1AgentCatalogDocument catalog =
            JsonSerializer.Deserialize<Demo1AgentCatalogDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("The Demo 1 agent catalog is empty.");

        Validate(catalog);
        return catalog;
    }

    public static ResolvedAgentDefinition Resolve(
        string repositoryRoot,
        Demo1AgentCatalogDocument catalog,
        string stage)
    {
        Demo1AgentDefinition definition = catalog.Definitions.Single(
            item => string.Equals(item.Stage, stage, StringComparison.Ordinal));
        string baseInstructions = ReadRepositoryFile(repositoryRoot, catalog.CanonicalBaseInstructionsPath);
        string? extension = definition.InstructionExtensionPath is null
            ? null
            : ReadRepositoryFile(repositoryRoot, definition.InstructionExtensionPath);
        string instructions = extension is null
            ? baseInstructions
            : $"{baseInstructions.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{extension.Trim()}";

        return new(definition, baseInstructions, instructions);
    }

    public static void Validate(Demo1AgentCatalogDocument catalog)
    {
        if (!string.Equals(catalog.SchemaVersion, "1.0.0", StringComparison.Ordinal) ||
            catalog.Definitions.Count != 2)
        {
            throw new InvalidDataException("Demo 1 requires exactly two version 1.0.0 definitions.");
        }

        Demo1AgentDefinition foundation = catalog.Definitions.Single(
            item => string.Equals(item.Name, FoundationAgentName, StringComparison.Ordinal));
        Demo1AgentDefinition ground = catalog.Definitions.Single(
            item => string.Equals(item.Name, GroundAgentName, StringComparison.Ordinal));

        if (foundation.InstructionExtensionPath is not null || foundation.FoundryIq is not null)
        {
            throw new InvalidDataException("The Foundation definition must not contain grounding.");
        }

        if (!string.Equals(foundation.Stage, "foundation", StringComparison.Ordinal) ||
            !string.Equals(ground.Stage, "ground", StringComparison.Ordinal) ||
            !string.Equals(foundation.Model, ModelDeploymentName, StringComparison.Ordinal) ||
            !string.Equals(ground.Model, ModelDeploymentName, StringComparison.Ordinal) ||
            !string.Equals(foundation.ReasoningEffort, FoundationReasoningEffort, StringComparison.Ordinal) ||
            !string.Equals(ground.ReasoningEffort, GroundReasoningEffort, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Foundation and Ground definitions do not use the required model and reasoning settings.");
        }

        Demo1FoundryIqDefinition iq = ground.FoundryIq
            ?? throw new InvalidDataException("The Ground definition must pin Foundry IQ.");
        if (ground.InstructionExtensionPath is null ||
            !string.Equals(iq.RetrievalReasoningEffort, GroundRetrievalReasoningEffort, StringComparison.Ordinal) ||
            !iq.Required ||
            !iq.FailClosed ||
            !iq.CitationsRequired ||
            !string.Equals(iq.ToolName, "knowledge_base_retrieve", StringComparison.Ordinal) ||
            !iq.AllowedSourceIds.SequenceEqual([ApprovedSourceId], StringComparer.Ordinal))
        {
            throw new InvalidDataException("The Ground definition must require retrieval and citations.");
        }
    }

    private static string ReadRepositoryFile(string repositoryRoot, string relativePath)
    {
        string root = Path.GetFullPath(repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The catalog path escapes the repository: {relativePath}");
        }

        return File.ReadAllText(fullPath).ReplaceLineEndings(Environment.NewLine);
    }
}

public sealed record Demo1AgentCatalogDocument(
    string SchemaVersion,
    string CanonicalBaseInstructionsPath,
    IReadOnlyList<Demo1AgentDefinition> Definitions);

public sealed record Demo1AgentDefinition(
    string Name,
    string DisplayName,
    string Stage,
    string Model,
    string ReasoningEffort,
    string? InstructionExtensionPath = null,
    Demo1FoundryIqDefinition? FoundryIq = null);

public sealed record Demo1FoundryIqDefinition(
    string KnowledgeBaseName,
    string RetrievalReasoningEffort,
    string KnowledgeSourceName,
    string ConnectionName,
    string ToolName,
    string ApiVersion,
    bool Required,
    bool FailClosed,
    bool CitationsRequired,
    IReadOnlyList<string> AllowedSourceIds);

public sealed record ResolvedAgentDefinition(
    Demo1AgentDefinition Definition,
    string CanonicalBaseInstructions,
    string Instructions);
