using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PublicSectorAgentDemos.Demo1.Web;

namespace PublicSectorAgentDemos.Demo1.Web.Tests;

public sealed class QualityAssessmentTests
{
    [Fact]
    public void ExactMpm005PromptSelectsReviewedThreeControlReference()
    {
        string root = FindRepositoryRoot();
        using JsonDocument fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "data", "ground", "v1", "fixture-set.json")));
        string prompt = fixtures.RootElement.GetProperty("fixtures").EnumerateArray()
            .Single(item => item.GetProperty("scenarioId").GetString() == "MPM-005")
            .GetProperty("prompt").GetString()!;
        AssessmentReferenceBuilder builder = new(new ConfigurationBuilder().Build());
        AssessmentReference reference = builder.Build(prompt, Answer("Foundation."), Answer("Ground."));

        Assert.Equal("MPM-005", reference.ScenarioId);
        Assert.Equal("treasury-consent", reference.ExpectedRoute);
        Assert.Equal(5, reference.ReviewRubric.Count);
        Assert.Contains(reference.ReviewRubric, item => item.Contains("GBP 3 million", StringComparison.Ordinal));
        Assert.Contains(reference.ReviewRubric, item => item.Contains("Parliament must be notified", StringComparison.Ordinal));
        Assert.Contains(reference.Limitations, item => item.Contains("identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RubricExplicitlyPenalizesGroundedButWrongConclusions()
    {
        Assert.Contains("grounded but wrong", QualityAssessmentRubric.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Use the names Foundation and Ground", QualityAssessmentRubric.Instructions, StringComparison.Ordinal);
        Assert.Contains("Do not call them Answer A or Answer B", QualityAssessmentRubric.Instructions, StringComparison.Ordinal);
        Assert.Contains("reference check", QualityAssessmentRubric.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("demo1-quality-v4", QualityAssessmentRubric.Version);
    }

    [Fact]
    public void SnapshotStoreEvictsTheOldestCompletedPair()
    {
        ComparisonSnapshotStore store = new();
        AssessmentReference reference = Reference();
        List<string> ids = [];
        for (int index = 0; index < DemoLimits.CompletedSnapshots + 1; index++)
        {
            string id = index.ToString("x32");
            ids.Add(id);
            store.Add(id, "owner", $"Prompt {index}", Answer("Foundation."), Answer("Ground."), reference);
        }

        Assert.False(store.TryGet(ids[0], "owner", out _));
        Assert.True(store.TryGet(ids[^1], "owner", out ComparisonSnapshot? newest));
        Assert.Equal($"Prompt {DemoLimits.CompletedSnapshots}", newest!.Prompt);
        Assert.False(store.TryGet(ids[^1], "different-owner", out _));
    }

    private static AgentAnswer Answer(string text) => AgentAnswer.Parse(JsonSerializer.Serialize(new
    {
        status = "completed",
        output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } }
    }));

    private static AssessmentReference Reference() => new(
        "Custom prompt. No reviewed expected outcome is available.", null, null, null, [], [],
        new(0, 0, [], "No evidence."), new(0, 0, [], "No evidence."), ["Limited references."]);

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
