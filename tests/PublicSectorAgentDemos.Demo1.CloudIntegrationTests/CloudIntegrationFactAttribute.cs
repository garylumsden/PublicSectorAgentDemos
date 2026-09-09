namespace PublicSectorAgentDemos.Demo1.CloudIntegrationTests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class CloudIntegrationFactAttribute : FactAttribute
{
    public CloudIntegrationFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_DEMO1_CLOUD_INTEGRATION"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Set RUN_DEMO1_CLOUD_INTEGRATION=true to enable the opt-in Demo 1 cloud test.";
            return;
        }

        string[] requiredVariables =
        [
            "AZURE_AI_FOUNDRY_ENDPOINT",
            "DEMO1_CITATION_IDENTITY_PATH"
        ];
        string[] missingVariables = requiredVariables
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();
        if (missingVariables.Length > 0)
        {
            Skip = $"Set the required Demo 1 cloud test variables: {string.Join(", ", missingVariables)}.";
        }
    }
}
