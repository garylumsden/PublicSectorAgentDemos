using System.Globalization;
using System.Text.Json;
using PublicSectorAgentDemos.Contracts.Demo4;
using PublicSectorAgentDemos.Demo4.Application;
using PublicSectorAgentDemos.Demo4.HostedAgents;

namespace PublicSectorAgentDemos.Demo4.Tests;

public sealed class CaseScenarioTests
{
    [Fact]
    public void CatalogContainsTheFiveApprovedCrossGovernmentScenarios()
    {
        CaseScenarioCatalog catalog = new(ScenarioFixturePath());
        IReadOnlyDictionary<string, CaseScenario> scenarios =
            catalog.Scenarios.ToDictionary(item => item.ScenarioId, StringComparer.Ordinal);

        Assert.Equal(5, scenarios.Count);
        AssertScenario(
            scenarios[TestData.BaselineScenarioId],
            "baselineWrite",
            "CG-8101",
            ExpectedMemoryAction.Skip,
            expectedRead: false,
            expectedWrite: true,
            ExpectedScenarioOutcome.Completed);
        AssertScenario(
            scenarios[TestData.RecurrenceScenarioId],
            "crossDepartmentRecurrence",
            "CG-8202",
            ExpectedMemoryAction.Read,
            expectedRead: true,
            expectedWrite: true,
            ExpectedScenarioOutcome.Completed);
        AssertScenario(
            scenarios[TestData.RuledOutScenarioId],
            "priorContextRuledOut",
            "CG-8303",
            ExpectedMemoryAction.Read,
            expectedRead: true,
            expectedWrite: true,
            ExpectedScenarioOutcome.Completed);
        AssertScenario(
            scenarios[TestData.UnrelatedScenarioId],
            "unrelatedControl",
            "CG-8404",
            ExpectedMemoryAction.Skip,
            expectedRead: false,
            expectedWrite: true,
            ExpectedScenarioOutcome.Completed);
        AssertScenario(
            scenarios[TestData.InvalidScenarioId],
            "invalidAssessment",
            "CG-8505",
            ExpectedMemoryAction.Skip,
            expectedRead: false,
            expectedWrite: false,
            ExpectedScenarioOutcome.Invalid);
    }

    [Fact]
    public void BaselineSkipsMemoryAndRecordsTheFirstControlFailure()
    {
        CaseScenario baseline = Catalog().GetRequired(TestData.BaselineScenarioId);

        Assert.Equal(ExpectedMemoryAction.Skip, baseline.ExpectedMemoryAction);
        Assert.False(baseline.ExpectedMemoryRead);
        Assert.True(baseline.ExpectedMemoryWrite);
        Assert.Equal(
            "supplier-assurance-evidence-missing-before-payment",
            baseline.ReasonCode);
    }

    [Fact]
    public void RecurrenceReadsMemoryAndRepeatsTheBaselineReasonCode()
    {
        CaseScenarioCatalog catalog = Catalog();
        CaseScenario baseline = catalog.GetRequired(TestData.BaselineScenarioId);
        CaseScenario recurrence = catalog.GetRequired(TestData.RecurrenceScenarioId);

        Assert.Equal(ExpectedMemoryAction.Read, recurrence.ExpectedMemoryAction);
        Assert.True(recurrence.ExpectedMemoryRead);
        Assert.True(recurrence.ExpectedMemoryWrite);
        Assert.Equal(baseline.ReasonCode, recurrence.ReasonCode);
        Assert.NotEqual(baseline.Pattern, recurrence.Pattern);
        Assert.NotEqual(baseline.CaseReference, recurrence.CaseReference);
    }

    [Fact]
    public void RuledOutCaseReadsMemoryAndReachesADifferentReasonCode()
    {
        CaseScenarioCatalog catalog = Catalog();
        CaseScenario baseline = catalog.GetRequired(TestData.BaselineScenarioId);
        CaseScenario ruledOut = catalog.GetRequired(TestData.RuledOutScenarioId);

        Assert.Equal(ExpectedMemoryAction.Read, ruledOut.ExpectedMemoryAction);
        Assert.True(ruledOut.ExpectedMemoryRead);
        Assert.True(ruledOut.ExpectedMemoryWrite);
        Assert.Equal("payment-release-control-not-engaged", ruledOut.ReasonCode);
        Assert.NotEqual(baseline.ReasonCode, ruledOut.ReasonCode);
    }

    [Fact]
    public void UnrelatedControlSkipsMemoryAndStillWritesOnce()
    {
        CaseScenarioCatalog catalog = Catalog();
        CaseScenario unrelated = catalog.GetRequired(TestData.UnrelatedScenarioId);

        Assert.Equal(ExpectedMemoryAction.Skip, unrelated.ExpectedMemoryAction);
        Assert.False(unrelated.ExpectedMemoryRead);
        Assert.True(unrelated.ExpectedMemoryWrite);
        Assert.Equal("records-retention-schedule-overdue", unrelated.ReasonCode);
        Assert.DoesNotContain(
            catalog.Scenarios.Where(item => item.ScenarioId != unrelated.ScenarioId),
            item => item.ReasonCode == unrelated.ReasonCode);
    }

    [Fact]
    public void EveryCompletedScenarioCarriesBoundedInterestingEvidence()
    {
        foreach (CaseScenario scenario in Catalog().Scenarios
                     .Where(item => item.ExpectedOutcome == ExpectedScenarioOutcome.Completed))
        {
            Assert.InRange(scenario.Evidence.Count, 2, 4);
            Assert.InRange(scenario.Confidence, 0.5m, 1m);
            Assert.InRange(scenario.Pattern.Length, 40, 256);
            Assert.Equal(
                scenario.Evidence.Count,
                scenario.Evidence.Select(item => item.EvidenceId)
                    .Distinct(StringComparer.Ordinal).Count());
            Assert.All(scenario.Evidence, evidence =>
                Assert.InRange(evidence.Summary.Length, 40, 240));
        }
    }

    [Fact]
    public void ScenarioFixtureRejectsALegacyCaseReferencePrefix()
    {
        string json = File.ReadAllText(ScenarioFixturePath());

        string invalid = json.Replace("CG-8101", "CP-8101", StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            CaseScenarioCatalog.FromJson(invalid));
    }

    [Fact]
    public void ContextCatalog_PreservesBoundedTextRules()
    {
        string json = File.ReadAllText(ContextFixturePath());
        CaseContextCatalog.FromJson(json);

        string invalid = json.Replace(
            "Grant payment release framing",
            @"Grant\u0001 payment release framing",
            StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            CaseContextCatalog.FromJson(invalid));
    }

    [Fact]
    public void RemoteApplicationPackageEmbedsTheCaseFixtures()
    {
        string repositoryRoot = RepositoryRoot();
        string project = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "deploy",
            "demo4",
            "application-package",
            "Demo4.Application.RemoteBuild.csproj"));
        string preparation = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "deploy",
            "demo4",
            "prepare-application-package.ps1"));

        Assert.Contains("Data\\fixture-set.json", project, StringComparison.Ordinal);
        Assert.Contains("Demo4.CaseFixtures.json", project, StringComparison.Ordinal);
        Assert.Contains("Data\\context-fixture-set.json", project, StringComparison.Ordinal);
        Assert.Contains("Demo4.ContextFixtures.json", project, StringComparison.Ordinal);
        Assert.Contains("fixture-set.json", preparation, StringComparison.Ordinal);
        Assert.Contains("context-fixture-set.json", preparation, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedPromptsAndDataNeverUseTheWordSynthetic()
    {
        string repositoryRoot = RepositoryRoot();
        string[] sources =
        [
            HostedAgentInstructions.Build(),
            File.ReadAllText(ScenarioFixturePath()),
            File.ReadAllText(ContextFixturePath()),
            File.ReadAllText(Path.Combine(
                repositoryRoot,
                "skills",
                "demo4",
                "case-pattern-guidance",
                "v1",
                "SKILL.md")),
            File.ReadAllText(Path.Combine(
                repositoryRoot,
                "skills",
                "demo4",
                "case-context-guidance",
                "v1",
                "SKILL.md")),
            CaseInvestigationToolProvider.AssessmentToolName,
            CaseInvestigationToolProvider.SearchToolName,
            CaseInvestigationToolProvider.ContextLabel,
            FoundryMemorySearchTool.ToolDescription,
            Demo4MemoryContract.StoreName,
            Demo4MemoryContract.Scope,
            NotebookRecordCodec.Kind
        ];

        Assert.All(sources, source =>
            Assert.DoesNotContain("synthetic", source, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HostedSourceFilesAndUiNeverUseTheWordSynthetic()
    {
        string repositoryRoot = RepositoryRoot();
        string[] roots =
        [
            Path.Combine(repositoryRoot, "src", "PublicSectorAgentDemos.Demo4.Application"),
            Path.Combine(repositoryRoot, "src", "PublicSectorAgentDemos.Demo4.HostedAgents"),
            Path.Combine(repositoryRoot, "data", "hosted"),
            Path.Combine(repositoryRoot, "skills", "demo4")
        ];
        string[] files =
        [
            .. roots.SelectMany(root =>
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)),
            Path.Combine(
                repositoryRoot,
                "src",
                "Shared",
                "PublicSectorAgentDemos.Contracts",
                "Demo4Contracts.cs")
        ];
        string[] scanned = files
            .Where(path =>
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) &&
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(scanned);
        Assert.All(scanned, path =>
        {
            Assert.DoesNotContain(
                "synthetic",
                Path.GetFileName(path),
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "synthetic",
                File.ReadAllText(path),
                StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void InstructionsNeverDictateAPerScenarioAnswer()
    {
        string instructions = HostedAgentInstructions.Build();

        Assert.All(
            Catalog().Scenarios,
            scenario =>
            {
                Assert.DoesNotContain(
                    scenario.ScenarioId,
                    instructions,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    scenario.CaseReference,
                    instructions,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    scenario.ReasonCode,
                    instructions,
                    StringComparison.OrdinalIgnoreCase);
            });
    }

    [Fact]
    public void MemoryStoreAndScopeUseCrossGovernmentNames()
    {
        Assert.Equal("demo4-cross-government-control-memory", Demo4MemoryContract.StoreName);
        Assert.Equal("demo4-cross-government-control-notebook", Demo4MemoryContract.Scope);
        Assert.Equal(
            "public-sector.demo4.cross-government-control-reference",
            NotebookRecordCodec.Kind);
    }

    [Fact]
    public async Task SearchReturnsOnlyDistinctNonCanonicalContext()
    {
        CaseInvestigationToolProvider provider = CreateProvider();
        string[] canonicalFields =
        [
            "scenarioId",
            "caseReference",
            "from",
            "to",
            "outcome",
            "pattern",
            "confidence",
            "reasonCode",
            "evidence",
            "evidenceId",
            "sourceId",
            "summary"
        ];

        foreach (CaseScenario scenario in Catalog().Scenarios)
        {
            CasePatternAssessment assessment = await provider.AssessAsync(
                scenario.ScenarioId,
                CancellationToken.None);
            CaseContextResult context = await provider.SearchAsync(
                scenario.ScenarioId,
                CancellationToken.None);
            string contextJson = JsonSerializer.Serialize(
                context,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            foreach (string field in canonicalFields)
            {
                Assert.DoesNotContain($"\"{field}\"", contextJson, StringComparison.Ordinal);
            }

            string[] canonicalValues =
            [
                assessment.ScenarioId,
                assessment.CaseReference,
                assessment.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                assessment.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                assessment.Outcome.ToString(),
                assessment.Pattern,
                assessment.Confidence.ToString(CultureInfo.InvariantCulture),
                assessment.ReasonCode,
                .. assessment.Evidence.SelectMany(item =>
                    new[] { item.EvidenceId, item.SourceId, item.Summary })
            ];
            foreach (string value in canonicalValues)
            {
                Assert.DoesNotContain(value, contextJson, StringComparison.OrdinalIgnoreCase);
            }

            Assert.NotEmpty(context.Observations);
            Assert.Contains("context only", context.Boundary, StringComparison.Ordinal);
            Assert.Contains("PREVIEW", context.Label, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ApprovedValidScenariosProduceAtomicCompletedAssessments()
    {
        CaseInvestigationToolProvider provider = CreateProvider();
        CaseScenarioCatalog catalog = Catalog();
        foreach (string scenarioId in new[]
                 {
                     TestData.BaselineScenarioId,
                     TestData.RecurrenceScenarioId,
                     TestData.RuledOutScenarioId,
                     TestData.UnrelatedScenarioId
                 })
        {
            CaseScenario scenario = catalog.GetRequired(scenarioId);
            CasePatternAssessment assessment = await provider.AssessAsync(
                scenarioId,
                CancellationToken.None);
            CasePatternAssessmentRequest request = new(
                $"investigation-{scenarioId.ToLowerInvariant()}",
                scenarioId,
                scenario.Prompt);
            HostedAgentTurnResult turn = Turn(
                assessment,
                scenario.ExpectedMemoryRead);

            ValidatedInvestigationResult result = AtomicAssessmentValidator.Validate(
                request,
                turn,
                TestData.Now);

            Assert.Equal(InvestigationOutcome.Completed, result.Assessment.Outcome);
            Assert.NotEmpty(result.Assessment.Evidence);
            Assert.Equal(
                scenario.ExpectedMemoryRead,
                turn.ToolExecutions!.Any(execution =>
                    execution.ToolName == Demo4MemoryContract.SearchToolName));
        }
    }

    [Fact]
    public async Task InvalidScenarioCannotProduceAMemoryWriteInput()
    {
        CaseInvestigationToolProvider provider = CreateProvider();
        CasePatternAssessment assessment = await provider.AssessAsync(
            TestData.InvalidScenarioId,
            CancellationToken.None);
        CasePatternAssessmentRequest request = new(
            "investigation-unverifiable-claim",
            assessment.ScenarioId,
            "Assess the configured unverifiable control claim.");

        Assert.Throws<HostedAssessmentProcessingException>(() =>
            AtomicAssessmentValidator.Validate(
                request,
                Turn(assessment),
                TestData.Now));
    }

    private static HostedAgentTurnResult Turn(
        CasePatternAssessment assessment,
        bool includeMemoryRead = false)
    {
        List<TrustedToolExecution> executions = [];
        if (includeMemoryRead)
        {
            executions.Add(new(
                "call-memory-1",
                Demo4MemoryContract.SearchToolName,
                true,
                """{"consulted":true,"records":[],"returnedCount":0}"""));
        }

        executions.Add(new(
            "call-assessment-1",
            CaseInvestigationToolProvider.AssessmentToolName,
            true,
            AtomicAssessmentValidator.SerializeAssessment(assessment)));
        return new(
            "completed",
            ["The assessment operation returned."],
            executions);
    }

    private static CaseInvestigationToolProvider CreateProvider() =>
        new(Catalog(), new CaseContextCatalog(ContextFixturePath()));

    private static CaseScenarioCatalog Catalog() => new(ScenarioFixturePath());

    private static void AssertScenario(
        CaseScenario scenario,
        string expectedCaseType,
        string expectedCaseReference,
        ExpectedMemoryAction expectedAction,
        bool expectedRead,
        bool expectedWrite,
        ExpectedScenarioOutcome expectedOutcome)
    {
        Assert.Equal(expectedCaseType, scenario.CaseType);
        Assert.Equal(expectedCaseReference, scenario.CaseReference);
        Assert.Equal(expectedAction, scenario.ExpectedMemoryAction);
        Assert.Equal(expectedRead, scenario.ExpectedMemoryRead);
        Assert.Equal(expectedWrite, scenario.ExpectedMemoryWrite);
        Assert.Equal(expectedOutcome, scenario.ExpectedOutcome);
    }

    private static string RepositoryRoot()
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

    private static string ScenarioFixturePath() =>
        FixturePath("fixture-set.json");

    private static string ContextFixturePath() =>
        FixturePath("context-fixture-set.json");

    private static string FixturePath(string fileName) =>
        Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "hosted",
            "v2",
            fileName);
}
