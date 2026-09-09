using System.Text.Json;

namespace Defra.EvalTests;

public sealed class Demo2PolicySeedEvalTests
{
    [Fact]
    public void SeedSet_CoversRequiredDemo2EvaluationCategories()
    {
        JsonElement[] cases = LoadCases();
        string[] categories = cases
            .Select(item => item.GetProperty("category").GetString()!)
            .ToArray();

        Assert.Contains("grounding", categories);
        Assert.Contains("refusal", categories);
        Assert.Contains("deterministic-guardrail", categories);
        Assert.Contains("provider-parity", categories);
        Assert.Contains("tool-routing", categories);
        Assert.Contains("envelope-validity", categories);
        Assert.Equal(cases.Length, cases.Select(CaseId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(cases, item => Assert.Equal("demo2", item.GetProperty("demo").GetString()));
    }

    [Fact]
    public void SeedExpectations_EncodeEmergencyBenignApprovalAndToolPolicyOutcomes()
    {
        JsonElement[] cases = LoadCases();
        JsonElement emergency = Case(cases, "D2-GUARD-001").GetProperty("expected");
        Assert.Equal("emergency", emergency.GetProperty("urgency").GetString());
        Assert.Equal("immediate-human-escalation", emergency.GetProperty("route").GetString());
        Assert.True(emergency.GetProperty("approvalRequired").GetBoolean());

        JsonElement benign = Case(cases, "D2-PARITY-001").GetProperty("expected");
        Assert.Equal("low-risk-clarification", benign.GetProperty("semanticOutcome").GetString());
        Assert.False(benign.GetProperty("approvalRequired").GetBoolean());

        JsonElement routing = Case(cases, "D2-ROUTE-001").GetProperty("expected");
        Assert.Equal("reserveSupportPackage", routing.GetProperty("approvalGatedTool").GetString());
        Assert.Equal(
            ["getSituationReports", "findAccommodation", "checkTransportCapacity"],
            routing.GetProperty("requiredTools").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.DoesNotContain(
            "reserveSupportPackage",
            routing.GetProperty("requiredTools").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void SeedEnvelope_RequiresExactSixBlockResponseWithToolTrace()
    {
        JsonElement expected = Case(LoadCases(), "D2-ENVELOPE-001").GetProperty("expected");
        string[] blocks = expected.GetProperty("requiredBlocks")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();

        Assert.Equal("1.0.0", expected.GetProperty("schemaVersion").GetString());
        Assert.Equal(6, expected.GetProperty("exactBlockCount").GetInt32());
        Assert.Equal(
            [
                "DirectAnswer",
                "EvidenceSummary",
                "ProvenanceAndFreshness",
                "CaveatsAndUncertainty",
                "RecommendedFollowUp",
                "ToolTrace"
            ],
            blocks);
    }

    private static string CaseId(JsonElement item) =>
        item.GetProperty("caseId").GetString()!;

    private static JsonElement Case(IEnumerable<JsonElement> cases, string caseId) =>
        cases.Single(item => string.Equals(CaseId(item), caseId, StringComparison.Ordinal));

    private static JsonElement[] LoadCases()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "evals",
            "flood-support",
            "v1",
            "seed.jsonl");
        return File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DEFRA-AI-Demos.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
