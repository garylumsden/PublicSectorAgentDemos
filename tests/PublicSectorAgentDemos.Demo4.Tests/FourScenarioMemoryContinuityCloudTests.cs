using Azure.Identity;
using PublicSectorAgentDemos.Contracts.Demo4;
using PublicSectorAgentDemos.Demo4.Application;

namespace PublicSectorAgentDemos.Demo4.Tests;

public sealed class FourScenarioMemoryContinuityCloudTests
{
    private const string SharedReasonCode = "supplier-assurance-evidence-missing-before-payment";
    private const string RuledOutReasonCode = "payment-release-control-not-engaged";
    private const string RetentionReasonCode = "records-retention-schedule-overdue";

    [Demo4CloudFact]
    [Trait("Category", "CloudIntegration")]
    public async Task FourVisibleScenariosRunInOrderAndProveMemoryContinuity()
    {
        using HttpClient httpClient = new();
        AzureFoundryAccessTokenProvider tokenProvider = new(new AzureCliCredential());
        FoundryHostedCasePatternAgentClient agentClient = new(
            httpClient,
            tokenProvider,
            new()
            {
                ResponsesEndpoint = GetRequiredVariable("DEMO4_HOSTED_AGENT_ENDPOINT"),
                ModelDeploymentName = GetRequiredVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
            });
        FoundryMemoryItemClient memoryClient = new(
            httpClient,
            tokenProvider,
            new() { ProjectEndpoint = GetRequiredVariable("FOUNDRY_PROJECT_ENDPOINT") });
        AssessmentProcessingService processor = new(
            agentClient,
            new InvestigationMemoryCoordinator(memoryClient, TimeProvider.System),
            TimeProvider.System);
        string run = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(40));

        AssessmentProcessingResult baseline = await processor.ProcessDetailedAsync(
            new(
                $"SEQ-{run}-1-BASELINE",
                "CASE-BASELINE-GRANTS",
                "Assess case CG-8101. A grants administration body released supplier payments "
                    + "and the assurance evidence is missing."),
            timeout.Token);

        Assert.Equal(InvestigationOutcome.Completed, baseline.Investigation.Assessment.Outcome);
        Assert.Equal("CG-8101", baseline.Investigation.Assessment.CaseReference);
        Assert.Equal(SharedReasonCode, baseline.Investigation.Assessment.ReasonCode);
        Assert.False(baseline.MemoryRead.Searched);
        Assert.Equal(
            MemoryContinuityReader.NotConsultedStatement,
            baseline.MemoryRead.ContinuityStatement);
        Assert.Equal(MemoryWriteStatus.Updated, baseline.MemoryWriteStatus);
        Assert.Equal("CG-8101", baseline.WrittenRecord.CaseReference);
        Assert.Equal(SharedReasonCode, baseline.WrittenRecord.ReasonCode);

        AssessmentProcessingResult recurrence = await processor.ProcessDetailedAsync(
            new(
                $"SEQ-{run}-2-RECURRENCE",
                "CASE-RECURRENCE-HOUSING",
                "Assess case CG-8202. Check whether another public body has already recorded "
                    + "a related control failure."),
            timeout.Token);

        Assert.Equal(InvestigationOutcome.Completed, recurrence.Investigation.Assessment.Outcome);
        Assert.Equal("CG-8202", recurrence.Investigation.Assessment.CaseReference);
        Assert.Equal(SharedReasonCode, recurrence.Investigation.Assessment.ReasonCode);
        Assert.True(recurrence.MemoryRead.Searched);
        Assert.Contains(
            recurrence.MemoryRead.References,
            reference =>
                reference.Verdict == MemoryMatchVerdict.MatchedRepeatControlFailure
                && reference.CaseReference == "CG-8101"
                && reference.ReasonCode == SharedReasonCode);
        Assert.Contains(
            "Memory changed the read of this case.",
            recurrence.MemoryRead.ContinuityStatement,
            StringComparison.Ordinal);
        Assert.Equal(MemoryWriteStatus.Updated, recurrence.MemoryWriteStatus);
        Assert.Equal("CG-8202", recurrence.WrittenRecord.CaseReference);

        AssessmentProcessingResult ruledOut = await processor.ProcessDetailedAsync(
            new(
                $"SEQ-{run}-3-RULED-OUT",
                "CASE-NEAR-MATCH-TRANSPORT",
                "Assess case CG-8303. Check whether a related control failure elsewhere "
                    + "explains the current evidence."),
            timeout.Token);

        Assert.Equal(InvestigationOutcome.Completed, ruledOut.Investigation.Assessment.Outcome);
        Assert.Equal("CG-8303", ruledOut.Investigation.Assessment.CaseReference);
        Assert.Equal(RuledOutReasonCode, ruledOut.Investigation.Assessment.ReasonCode);
        Assert.True(ruledOut.MemoryRead.Searched);
        Assert.DoesNotContain(
            ruledOut.MemoryRead.References,
            reference => reference.Verdict == MemoryMatchVerdict.MatchedRepeatControlFailure);
        Assert.Contains(
            ruledOut.MemoryRead.References,
            reference =>
                reference.Verdict == MemoryMatchVerdict.RuledOutDifferentControl
                && reference.ReasonCode == SharedReasonCode);
        Assert.All(
            ruledOut.MemoryRead.References,
            reference => Assert.True(
                reference.Verdict == MemoryMatchVerdict.RuledOutDifferentControl
                    ? reference.ReasonCode != RuledOutReasonCode
                    : reference.CaseReference == "CG-8303",
                $"Reference {reference.RecordId} carried verdict {reference.Verdict} "
                    + $"for case {reference.CaseReference} and reason {reference.ReasonCode}."));
        Assert.Equal(MemoryWriteStatus.Updated, ruledOut.MemoryWriteStatus);
        Assert.Equal("CG-8303", ruledOut.WrittenRecord.CaseReference);

        AssessmentProcessingResult skipped = await processor.ProcessDetailedAsync(
            new(
                $"SEQ-{run}-4-SKIP",
                "CASE-UNRELATED-CONTROL",
                "Assess case CG-8404. A public audit body reports an overdue records "
                    + "retention schedule."),
            timeout.Token);

        Assert.Equal(InvestigationOutcome.Completed, skipped.Investigation.Assessment.Outcome);
        Assert.Equal("CG-8404", skipped.Investigation.Assessment.CaseReference);
        Assert.Equal(RetentionReasonCode, skipped.Investigation.Assessment.ReasonCode);
        Assert.False(skipped.MemoryRead.Searched);
        Assert.DoesNotContain(
            skipped.Investigation.ToolExecutions,
            execution => execution.ToolName == Demo4MemoryContract.SearchToolName);
        Assert.Equal(MemoryWriteStatus.Updated, skipped.MemoryWriteStatus);
        Assert.Equal("CG-8404", skipped.WrittenRecord.CaseReference);

        MemoryNotebookListing listing = await memoryClient.ListValidRecordsAsync(
            DateTimeOffset.UtcNow,
            timeout.Token);
        string[] recordIds = listing.Records
            .Select(entry => entry.Record.RecordId)
            .ToArray();

        Assert.Contains(baseline.WrittenRecord.RecordId, recordIds);
        Assert.Contains(recurrence.WrittenRecord.RecordId, recordIds);
        Assert.All(
            listing.Records,
            entry =>
            {
                Assert.Equal(NotebookRecordCodec.Kind, entry.Record.Kind);
                Assert.Equal(NotebookRecordCodec.Version, entry.Record.Version);
            });
    }

    private static string GetRequiredVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is required.");
}
