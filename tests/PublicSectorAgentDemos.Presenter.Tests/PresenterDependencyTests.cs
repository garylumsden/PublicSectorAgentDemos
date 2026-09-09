using System.Text.Json;

namespace PublicSectorAgentDemos.Presenter.Tests;

public sealed class PresenterDependencyTests
{
    private static readonly string[] ForbiddenNames =
    [
        "PublicSectorAgentDemos.Demo1",
        "PublicSectorAgentDemos.Demo2",
        "Coordinate",
        "GovernanceCouncil",
        "PublicSectorAgentDemos.Demo4"
    ];

    [Fact]
    public void Presenter_HasNoDemoProjectOrPackageGraphDependencies()
    {
        string root = FindRepositoryRoot();
        string projectPath = Path.Combine(
            root,
            "src",
            "PublicSectorAgentDemos.Presenter",
            "PublicSectorAgentDemos.Presenter.csproj");
        string project = File.ReadAllText(projectPath);

        Assert.Contains(
            "PublicSectorAgentDemos.Observability.csproj",
            project,
            StringComparison.Ordinal);
        Assert.Contains("PublicSectorAgentDemos.Identity.csproj", project, StringComparison.Ordinal);
        Assert.DoesNotContain("<PackageReference Include=\"Azure.Identity\"", project, StringComparison.Ordinal);
        Assert.All(
            ForbiddenNames,
            name => Assert.DoesNotContain(name, project, StringComparison.OrdinalIgnoreCase));

        string assetsPath = Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            "obj",
            "project.assets.json");
        Assert.True(File.Exists(assetsPath), "Restore must create the presenter package graph.");
        using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        string graph = assets.RootElement.GetRawText();
        Assert.All(
            ForbiddenNames,
            name => Assert.DoesNotContain(name, graph, StringComparison.OrdinalIgnoreCase));
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
