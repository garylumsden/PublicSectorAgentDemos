namespace PublicSectorAgentDemos.ArchitectureTests;

public sealed class ApplicationInsightsWiringTests
{
    [Fact]
    public void EveryFoundryProjectUsesIdentityBasedApplicationInsights()
    {
        string root = FindRepositoryRoot();
        string[] templates =
        [
            "infra/demo1-standalone-resources.bicep",
            "infra/demo4/resources.bicep"
        ];

        foreach (string relativePath in templates)
        {
            string source = File.ReadAllText(Path.Combine(root, relativePath));
            Assert.Contains("Microsoft.Insights/components@2020-02-02", source);
            Assert.Contains("DisableLocalAuth: true", source);
            Assert.Contains("WorkspaceResourceId:", source);
            Assert.Contains("category: 'AppInsights'", source);
            Assert.Contains("authType: 'ProjectManagedIdentity'", source);
            Assert.Contains("ApplicationInsightsConnectionString:", source);
            Assert.Contains("3913510d-42f4-4e42-8a64-420c390055eb", source);
            Assert.DoesNotContain("output APPLICATIONINSIGHTS_CONNECTION_STRING", source);
        }
    }

    [Fact]
    public void EveryOwnedApplicationExportsThroughAzureMonitor()
    {
        string root = FindRepositoryRoot();
        string helper = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Shared",
            "PublicSectorAgentDemos.Observability",
            "DemoTelemetry.cs"));
        Assert.Contains("UseAzureMonitor", helper);
        Assert.Contains("DefaultAzureCredential", helper);
        Assert.Contains("APPLICATIONINSIGHTS_CONNECTION_STRING", helper);

        string presenter = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PublicSectorAgentDemos.Presenter",
            "Program.cs"));
        Assert.Contains("AddDemoObservability", presenter);

        string hosted = File.ReadAllText(Path.Combine(root, "infra", "demo4", "resources.bicep"));
        Assert.Contains("APPLICATIONINSIGHTS_CONNECTION_STRING", hosted);
        Assert.Contains("applicationTelemetryPublisher", hosted);
        string hostedProgram = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PublicSectorAgentDemos.Demo4.HostedAgents",
            "Program.cs"));
        Assert.DoesNotContain("AddDemoObservability", hostedProgram);
    }

    [Fact]
    public void Demo1TracesDoNotRegressToConnect()
    {
        string root = FindRepositoryRoot();
        string demo1 = File.ReadAllText(Path.Combine(
            root,
            "infra",
            "demo1-standalone-resources.bicep"));
        Assert.Equal(
            1,
            CountOccurrences(
                demo1,
                "resource applicationInsightsConnection " +
                "'Microsoft.CognitiveServices/accounts/projects/connections@2025-09-01'"));
        Assert.Contains("category: 'AppInsights'", demo1);
        Assert.Contains("authType: 'ProjectManagedIdentity'", demo1);

        string readiness = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Invoke-DemoReady.ps1"));
        Assert.Contains("APPLICATIONINSIGHTS_CONNECTION_STRING = $demo1AppInsights", readiness);
        Assert.Contains("Get-DemoReadyApplicationInsightsConnectionString", readiness);
        Assert.DoesNotContain("'app-insights', 'query'", readiness);
    }

    [Theory]
    [InlineData("infra/demo1-standalone-resources.bicep", "resource aiProject ", "modelDeployment")]
    [InlineData(@"src\PublicSectorAgentDemos.Demo3.Coordinate\infra\resources.bicep", "module embeddingDeployment ", "contentPolicy")]
    [InlineData(@"src\PublicSectorAgentDemos.Demo3.Coordinate\infra\resources.bicep", "resource aiProject ", "nanoDeployment")]
    public void AccountChildrenAreSequencedForInitialProvisioning(
        string relativePath,
        string declaration,
        string dependency)
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath))
            .ReplaceLineEndings("\n");
        int start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = source.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start);

        Assert.Matches(
            $@"(?s)dependsOn:\s*\[[^\]]*\b{System.Text.RegularExpressions.Regex.Escape(dependency)}\b[^\]]*\]",
            source[start..(end + 2)]);
    }

    [Fact]
    public void HostedLifecycleUsesRootHooksThatSurviveAgentConfigurationWriteback()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "deploy", "demo4", "azure.yaml")).ReplaceLineEndings("\n");
        int hooksStart = source.IndexOf("\nhooks:\n", StringComparison.Ordinal);
        int hooksEnd = source.IndexOf("\nworkflows:\n", hooksStart, StringComparison.Ordinal);
        Assert.True(hooksStart >= 0 && hooksEnd > hooksStart);
        string hooks = source[hooksStart..hooksEnd];

        Assert.Contains("predeploy:", hooks, StringComparison.Ordinal);
        Assert.Contains("run: ./prepare-agent-package.ps1", hooks, StringComparison.Ordinal);
        Assert.Contains("postup:", hooks, StringComparison.Ordinal);
        Assert.Contains("run: ./agent-package/configure-hosted-agent.ps1", hooks, StringComparison.Ordinal);
    }

    [Fact]
    public void PresenterIdentityCanPublishToTheIdentityProtectedTelemetryResource()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "infra", "demo1-standalone-resources.bicep")).ReplaceLineEndings("\n");
        int start = source.IndexOf("resource presenterTelemetryPublisher ", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = source.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string resource = source[start..(end + 2)];

        Assert.Contains("scope: applicationInsights", resource, StringComparison.Ordinal);
        Assert.Contains("principalId: userPrincipalId", resource, StringComparison.Ordinal);
        Assert.Contains("principalType: 'User'", resource, StringComparison.Ordinal);
        Assert.Contains("monitoringMetricsPublisherRoleId", resource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("infra/demo1-main.bicep")]
    [InlineData(@"src\PublicSectorAgentDemos.Demo2.Act\infra\main.bicep")]
    [InlineData(@"src\PublicSectorAgentDemos.Demo3.Coordinate\infra\main.bicep")]
    [InlineData("infra/demo4/main.bicep")]
    public void OwnedDemoResourceGroupsRetainTheApprovedSecurityControlTag(string relativePath)
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath));
        Assert.Contains("SecurityControl: 'Ignore'", source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PublicSectorAgentDemos.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
