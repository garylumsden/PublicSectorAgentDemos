namespace PublicSectorAgentDemos.Demo4.Tests;

internal sealed class Demo4CloudFactAttribute : FactAttribute
{
    public Demo4CloudFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_DEMO4_CLOUD_INTEGRATION") != "true")
        {
            Skip = "Set RUN_DEMO4_CLOUD_INTEGRATION=true to run the Demo 4 cloud test.";
            return;
        }

        string[] required =
        [
            "DEMO4_HOSTED_AGENT_ENDPOINT",
            "FOUNDRY_PROJECT_ENDPOINT",
            "AZURE_AI_MODEL_DEPLOYMENT_NAME"
        ];
        string[] missing = required
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();
        if (missing.Length > 0)
        {
            Skip = $"Missing required environment values: {string.Join(", ", missing)}.";
        }
    }
}
