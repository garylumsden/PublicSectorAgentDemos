using Microsoft.Extensions.Options;

namespace Demo2.Web.Configuration;

public sealed class Demo2Options
{
    public const string SectionName = "Demo2";

    public string RulesPath { get; set; } = "config/domain/flood-support-rules.v1.json";

    public string ToolPolicyPath { get; set; } = "config/policies/demo2-tools.v1.json";

    public int MaxComplaintCharacters { get; set; } = 6_000;

    public int ApprovalLifetimeMinutes { get; set; } = 30;

    public Demo2AgentOptions Agent { get; set; } = new();

    public Demo2ToolboxOptions Toolbox { get; set; } = new();

    public Demo2PersistenceOptions Persistence { get; set; } = new();

    public Demo2AuditOptions Audit { get; set; } = new();
}

public sealed class Demo2AgentOptions
{
    public string Mode { get; set; } = Demo2AgentModes.LocalContract;

    public string Name { get; set; } = "cross-government-flood-support";

    public string ProjectEndpoint { get; set; } = string.Empty;

    public string ModelDeploymentName { get; set; } = string.Empty;

    public string ReasoningEffort { get; set; } = "low";

    public int MaxOutputTokens { get; set; } = 4_000;

    public int MaxToolCalls { get; set; } = 4;
}

public static class Demo2AgentModes
{
    public const string Azure = "Azure";
    public const string LocalContract = "LocalContract";
}

public sealed class Demo2ToolboxOptions
{
    public string Name { get; set; } = "flood-support";

    public string Endpoint { get; set; } = "http://localhost:5100/mcp/flood-support";

    public string Audience { get; set; } = string.Empty;

    public string DispatchToolName { get; set; } = "reserveSupportPackage";

    public int ConnectionTimeoutSeconds { get; set; } = 30;
}

public sealed class Demo2PersistenceOptions
{
    public string Provider { get; set; } = Demo2PersistenceProviders.InMemory;

    public string CosmosEndpoint { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = "defra-ai-demos";

    public string ContainerName { get; set; } = "demo2-flood-support-cases";
}

public static class Demo2PersistenceProviders
{
    public const string Cosmos = "Cosmos";
    public const string InMemory = "InMemory";
}

public sealed class Demo2AuditOptions
{
    public string Path { get; set; } = "audit/demo2-audit.jsonl";
}

public sealed class Demo2OptionsValidator : IValidateOptions<Demo2Options>
{
    public ValidateOptionsResult Validate(string? name, Demo2Options options)
    {
        List<string> failures = [];

        ValidateRelativePath(options.RulesPath, nameof(options.RulesPath), failures);
        ValidateRelativePath(options.ToolPolicyPath, nameof(options.ToolPolicyPath), failures);
        ValidateSafeName(options.Agent.Name, "Agent.Name", failures);
        ValidateSafeName(options.Toolbox.Name, "Toolbox.Name", failures);
        ValidateSafeName(options.Toolbox.DispatchToolName, "Toolbox.DispatchToolName", failures);
        if (options.Toolbox.DispatchToolName != "reserveSupportPackage")
        {
            failures.Add("Toolbox.DispatchToolName must be reserveSupportPackage.");
        }

        if (options.MaxComplaintCharacters is < 100 or > 20_000)
        {
            failures.Add("MaxComplaintCharacters must be between 100 and 20000.");
        }

        if (options.ApprovalLifetimeMinutes is < 1 or > 1_440)
        {
            failures.Add("ApprovalLifetimeMinutes must be between 1 and 1440.");
        }

        if (options.Agent.MaxOutputTokens is < 100 or > 8_000)
        {
            failures.Add("Agent.MaxOutputTokens must be between 100 and 8000.");
        }

        if (options.Agent.MaxToolCalls is < 1 or > 10)
        {
            failures.Add("Agent.MaxToolCalls must be between 1 and 10.");
        }

        if (options.Agent.ReasoningEffort is not ("low" or "medium" or "high"))
        {
            failures.Add("Agent.ReasoningEffort must be low, medium, or high.");
        }

        if (!string.Equals(options.Agent.Mode, Demo2AgentModes.Azure, StringComparison.Ordinal) &&
            !string.Equals(options.Agent.Mode, Demo2AgentModes.LocalContract, StringComparison.Ordinal))
        {
            failures.Add("Agent.Mode must be Azure or LocalContract.");
        }

        if (string.Equals(options.Agent.Mode, Demo2AgentModes.Azure, StringComparison.Ordinal) &&
            !IsHttpsUri(options.Agent.ProjectEndpoint))
        {
            failures.Add("Agent.ProjectEndpoint must be an absolute HTTPS URI in Azure mode.");
        }

        if (string.Equals(options.Agent.Mode, Demo2AgentModes.Azure, StringComparison.Ordinal))
        {
            ValidateSafeName(
                options.Agent.ModelDeploymentName,
                "Agent.ModelDeploymentName",
                failures);
        }

        if (!IsHttpUri(options.Toolbox.Endpoint))
        {
            failures.Add("Toolbox.Endpoint must be an absolute HTTP or HTTPS URI.");
        }

        if (options.Toolbox.ConnectionTimeoutSeconds is < 1 or > 120)
        {
            failures.Add("Toolbox.ConnectionTimeoutSeconds must be between 1 and 120.");
        }

        if (Uri.TryCreate(options.Toolbox.Endpoint, UriKind.Absolute, out Uri? toolboxUri) &&
            toolboxUri.Scheme == Uri.UriSchemeHttps &&
            !(options.Toolbox.Audience.StartsWith("api://", StringComparison.OrdinalIgnoreCase) ||
              options.Toolbox.Audience.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add("Toolbox.Audience must be an api:// or https:// managed identity audience.");
        }

        if (!string.Equals(options.Persistence.Provider, Demo2PersistenceProviders.Cosmos, StringComparison.Ordinal) &&
            !string.Equals(options.Persistence.Provider, Demo2PersistenceProviders.InMemory, StringComparison.Ordinal))
        {
            failures.Add("Persistence.Provider must be Cosmos or InMemory.");
        }

        if (string.Equals(options.Persistence.Provider, Demo2PersistenceProviders.Cosmos, StringComparison.Ordinal))
        {
            if (!IsHttpsUri(options.Persistence.CosmosEndpoint))
            {
                failures.Add("Persistence.CosmosEndpoint must be an absolute HTTPS URI in Cosmos mode.");
            }

            ValidateSafeName(options.Persistence.DatabaseName, "Persistence.DatabaseName", failures);
            ValidateSafeName(options.Persistence.ContainerName, "Persistence.ContainerName", failures);
        }

        ValidateRelativePath(options.Audit.Path, "Audit.Path", failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsHttpUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsHttpsUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme == Uri.UriSchemeHttps;

    private static void ValidateRelativePath(string value, string name, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            failures.Add($"{name} must be a safe relative path.");
        }
    }

    private static void ValidateSafeName(string value, string name, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.'))
        {
            failures.Add($"{name} must contain only safe identifier characters.");
        }
    }
}

public static class Demo2EnvironmentConfiguration
{
    public static void Apply(
        Demo2Options options,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        ApplyIfPresent(
            configuration["AZURE_AI_PROJECT_ENDPOINT"],
            value =>
            {
                options.Agent.ProjectEndpoint = value;
                options.Agent.Mode = Demo2AgentModes.Azure;
            });
        ApplyIfPresent(
            configuration["AZURE_AI_MODEL_DEPLOYMENT_NAME"],
            value => options.Agent.ModelDeploymentName = value);
        ApplyIfPresent(
            configuration["AZURE_AI_REASONING_EFFORT"],
            value => options.Agent.ReasoningEffort = value.ToLowerInvariant());
        ApplyIfPresent(
            configuration["DEFRA_TOOLS_MCP_URL"],
            value => options.Toolbox.Endpoint = value);
        ApplyIfPresent(
            configuration["DEFRA_TOOLS_MCP_FLOOD_SUPPORT_URL"],
            value => options.Toolbox.Endpoint = value);
        ApplyIfPresent(
            configuration["DEFRA_TOOLS_AUDIENCE"],
            value => options.Toolbox.Audience = value);
        ApplyIfPresent(
            configuration["AZURE_COSMOS_ENDPOINT"],
            value =>
            {
                options.Persistence.CosmosEndpoint = value;
                options.Persistence.Provider = Demo2PersistenceProviders.Cosmos;
            });
        ApplyIfPresent(
            configuration["AZURE_COSMOS_DATABASE_NAME"],
            value => options.Persistence.DatabaseName = value);
        ApplyIfPresent(
            configuration["AZURE_COSMOS_CONTAINER_NAME"],
            value => options.Persistence.ContainerName = value);

        if (environment.IsEnvironment("Testing"))
        {
            options.Agent.Mode = Demo2AgentModes.LocalContract;
            options.Persistence.Provider = Demo2PersistenceProviders.InMemory;
        }
        else if (!environment.IsDevelopment())
        {
            options.Agent.Mode = Demo2AgentModes.Azure;
            options.Persistence.Provider = Demo2PersistenceProviders.Cosmos;
        }
    }

    private static void ApplyIfPresent(string? value, Action<string> apply)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            apply(value.Trim());
        }
    }

}
