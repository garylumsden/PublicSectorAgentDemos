namespace PublicSectorAgentDemos.ArchitectureTests;

public sealed class ResourceGroupNamingTests
{
    [Fact]
    public void EveryOwnedDemoUsesTheEnvironmentNameAsTheResourceGroupSuffix()
    {
        string root = FindRepositoryRoot();
        string[] templates =
        [
            Path.Combine(root, "infra", "demo1-main.bicep"),
            Path.Combine(root, "src", "PublicSectorAgentDemos.Demo2.Act", "infra", "main.bicep"),
            Path.Combine(root, "src", "PublicSectorAgentDemos.Demo3.Coordinate", "infra", "main.bicep"),
            Path.Combine(root, "infra", "demo4", "main.bicep")
        ];

        foreach (string template in templates)
        {
            string content = File.ReadAllText(template);
            Assert.Contains("'rg-${environmentName}'", content, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PublicSectorAgentDemos.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
