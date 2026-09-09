using System.Text.Json;
namespace PublicSectorAgentDemos.ContractTests;

public sealed class FixtureVersionTests
{
    [Fact]
    public void EveryFixtureSetIsVersioned()
    {
        string repositoryRoot = FindRepositoryRoot();
        string dataPath = Path.Combine(repositoryRoot, "data");
        string[] fixtureFiles = Directory.GetFiles(
            dataPath,
            "*.json",
            SearchOption.AllDirectories);

        Assert.NotEmpty(fixtureFiles);

        foreach (string fixtureFile in fixtureFiles)
        {
            string json = File.ReadAllText(fixtureFile);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string? version = root.TryGetProperty(
                "dataVersion",
                out JsonElement dataVersion)
                ? dataVersion.GetString()
                : root.TryGetProperty("version", out JsonElement fixtureVersion)
                    ? fixtureVersion.GetString()
                    : null;
            Assert.True(
                !string.IsNullOrWhiteSpace(version),
                fixtureFile);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

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
