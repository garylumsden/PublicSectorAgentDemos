using System.Text.Json;
using System.Text.Json.Serialization;
using Demo2.Web.Domain;
using Demo2.Web.Agent;
using Defra.Contracts.FloodSupport;

namespace Defra.EvalTests;

public sealed class Demo2EvalTests
{
    [Theory]
    [InlineData("AREA-1001", "Riverton flood-displacement; accommodation reaches capacity at 18:00.", WelfareUrgency.High, true)]
    [InlineData("AREA-1001", "Repeated unmet accommodation needs require review.", WelfareUrgency.High, true)]
    [InlineData("AREA-1002", "Flood water is stable. Temporary accommodation has spare capacity and scheduled transport remains available. Review support coordination during normal operating hours.", WelfareUrgency.Medium, false)]
    [InlineData("AREA-1002", "Meadowfield information only: confirm support is needed.", WelfareUrgency.Low, false)]
    [InlineData(null, "Confirm the incident area and the support need.", WelfareUrgency.Unclassified, false)]
    public async Task LocalFixtureAssessment_ExercisesFloodScenarioSafety(string? area, string text, WelfareUrgency expected, bool approvalExpected)
    {
        Demo2EvalFixture fixture = Demo2EvalFixture.Load();
        EmergencyPolicyResult emergency = fixture.Policy.EvaluateEmergency(text);
        bool approvalRequested = false;
        SyntheticContractWelfareAgentClient client = new();
        AgentAssessment assessment = await client.AssessAsync(new("FLOOD-EVAL", area, text, DateTimeOffset.UtcNow, 7,
            emergency.IsEmergency ? WelfareUrgency.High : WelfareUrgency.Unclassified,
            emergency.IsEmergency ? WelfareRoute.ImmediateHumanEscalation : null,
            "approval-33333333333333333333333333333333", "INC-2001", null, null), (request, _) =>
        {
            Assert.True(FloodSupportCatalogue.TryParseApprovalArguments(request.Arguments, out SupportPackageRequest? parsed));
            Assert.NotNull(parsed);
            approvalRequested = true;
            return Task.FromResult(new AgentMcpApprovalDecision(false, "eval-no-reservation"));
        });
        Assert.Equal(approvalExpected, approvalRequested);
        Assert.Equal(expected, fixture.Policy.ApplyPostModel(assessment, null).Urgency);
        Assert.Null(assessment.Dispatch);
        if (area == "AREA-1001")
        {
            Assert.Contains(assessment.Evidence, evidence => evidence.Contains("UNMET: 120", StringComparison.Ordinal));
            Assert.Contains(assessment.Evidence, evidence => evidence.Contains("source verification", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void EmergencySeed_BypassesModelAsHighImmediateEscalation()
    {
        Demo2EvalFixture fixture = Demo2EvalFixture.Load();
        JsonElement evalCase = fixture.SeedCase("D2-GUARD-001");
        JsonElement scenario = fixture.Scenario(
            evalCase.GetProperty("input").GetProperty("scenarioId").GetString()!);

        EmergencyPolicyResult emergency = fixture.Policy.EvaluateEmergency(
            scenario.GetProperty("situationText").GetString()!);
        WelfareUrgency urgency = emergency.IsEmergency
            ? WelfareUrgency.High
            : WelfareUrgency.Unclassified;
        WelfareRoute route = emergency.IsEmergency
            ? WelfareRoute.ImmediateHumanEscalation
            : WelfareRoute.RequestClarification;

        Assert.True(emergency.IsEmergency);
        Assert.Equal(WelfareUrgency.High, urgency);
        Assert.Equal(WelfareRoute.ImmediateHumanEscalation, route);
        Assert.Equal(
            "immediate-human-escalation",
            evalCase.GetProperty("expected").GetProperty("route").GetString());
        Assert.True(evalCase.GetProperty("expected").GetProperty("approvalRequired").GetBoolean());
    }

    [Fact]
    public void RepeatOffenderSeed_RaisesLowModelResultToHigh()
    {
        Demo2EvalFixture fixture = Demo2EvalFixture.Load();
        JsonElement evalCase = fixture.SeedCase("D2-ROUTE-001");
        AgentAssessment lowAgentResult = new(
            WelfareUrgency.Low,
            WelfareRoute.RecordAndClose,
            "Synthetic low result before deterministic repeat check.",
            ["synthetic history"],
            fixture.Rules.RepeatComplaintMinimumCount,
            true,
            false,
            "response-eval-repeat",
            "conversation-eval-repeat");

        PostModelPolicyResult result = fixture.Policy.ApplyPostModel(lowAgentResult, null);

        Assert.Equal(WelfareUrgency.High, result.Urgency);
        Assert.Equal(WelfareRoute.PriorityInspectorReview, result.Route);
        Assert.True(result.RepeatEscalated);
        string[] requiredTools = evalCase
            .GetProperty("expected")
            .GetProperty("requiredTools")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert.Equal(
            ["getSituationReports", "findAccommodation", "checkTransportCapacity"],
            requiredTools);
        Assert.Equal(
            "reserveSupportPackage",
            evalCase.GetProperty("expected").GetProperty("approvalGatedTool").GetString());
    }

    [Fact]
    public void BenignLowRiskSeed_RemainsLowWithoutApproval()
    {
        Demo2EvalFixture fixture = Demo2EvalFixture.Load();
        JsonElement evalCase = fixture.SeedCase("D2-PARITY-001");
        AgentAssessment benign = new(
            WelfareUrgency.Low,
            WelfareRoute.RecordAndClose,
            "Fictional low-risk information request.",
            ["information-only situation report", "synthetic capacity report"],
            fixture.Rules.RepeatComplaintMinimumCount - 1,
            true,
            false,
            "response-eval-benign",
            "conversation-eval-benign");

        PostModelPolicyResult result = fixture.Policy.ApplyPostModel(benign, null);

        Assert.Equal(WelfareUrgency.Low, result.Urgency);
        Assert.Equal(WelfareRoute.RecordAndClose, result.Route);
        Assert.False(result.RepeatEscalated);
        Assert.Equal(
            "low-risk-clarification",
            evalCase.GetProperty("expected").GetProperty("semanticOutcome").GetString());
        Assert.False(
            evalCase.GetProperty("expected").GetProperty("approvalRequired").GetBoolean());
    }

    private sealed class Demo2EvalFixture
    {
        private readonly JsonElement[] _seedCases;
        private readonly JsonElement[] _scenarios;

        private Demo2EvalFixture(
            WelfareRuleSet rules,
            JsonElement[] seedCases,
            JsonElement[] scenarios)
        {
            Rules = rules;
            Policy = new WelfareDeterministicPolicy(new InlineRuleProvider(rules));
            _seedCases = seedCases;
            _scenarios = scenarios;
        }

        public WelfareRuleSet Rules { get; }

        public WelfareDeterministicPolicy Policy { get; }

        public static Demo2EvalFixture Load()
        {
            string root = FindRepositoryRoot();
            JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            };
            WelfareRuleSet rules = JsonSerializer.Deserialize<WelfareRuleSet>(
                    File.ReadAllText(
                        Path.Combine(root, "config", "domain", "flood-support-rules.v1.json")),
                    serializerOptions)
                ?? throw new InvalidOperationException("Flood-support rules were not loaded.");
            JsonElement[] seedCases = File
                .ReadLines(Path.Combine(root, "evals", "flood-support", "v1", "seed.jsonl"))
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToArray();
            using JsonDocument scenariosDocument = JsonDocument.Parse(
                File.ReadAllText(
                    Path.Combine(root, "data", "flood-support", "v1", "scenarios.json")));
            JsonElement[] scenarios = scenariosDocument.RootElement
                .GetProperty("scenarios")
                .EnumerateArray()
                .Select(item => item.Clone())
                .ToArray();
            return new(rules, seedCases, scenarios);
        }

        public JsonElement SeedCase(string caseId) =>
            _seedCases.Single(item =>
                item.GetProperty("caseId").GetString() == caseId);

        public JsonElement Scenario(string scenarioId) =>
            _scenarios.Single(item =>
                item.GetProperty("scenarioId").GetString() == scenarioId);

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

        private sealed class InlineRuleProvider(WelfareRuleSet rules) : IWelfareRuleProvider
        {
            public WelfareRuleSet Rules { get; } = rules;
        }
    }
}
