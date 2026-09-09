using PublicSectorAgentDemos.Contracts.Demo4;
using PublicSectorAgentDemos.Demo4.Application;

namespace PublicSectorAgentDemos.Demo4.Tests;

public sealed class InvestigationMemoryCoordinatorTests
{
    [Fact]
    public async Task CompletedAssessmentWritesCanonicalTypedRecordAfterLegitimateSkip()
    {
        RecordingMemoryClient client = new();
        InvestigationMemoryCoordinator coordinator = new(
            client,
            new FixedTimeProvider(TestData.Now));

        MemoryWriteOutcome outcome = await coordinator.SaveAsync(
            TestData.Result(InvestigationOutcome.Completed, []),
            CancellationToken.None);

        Assert.Equal(MemoryWriteStatus.Updated, outcome.Status);
        NotebookRecordEnvelope record = Assert.Single(client.Records);
        Assert.Same(record, outcome.Record);
        Assert.Equal(NotebookRecordCodec.Serialize(record), outcome.SerializedRecord);
        Assert.Equal(NotebookRecordCodec.Kind, record.Kind);
        Assert.Equal("CASE-BASELINE-GRANTS", record.ScenarioId);
    }

    [Theory]
    [InlineData(InvestigationOutcome.Refused)]
    [InlineData(InvestigationOutcome.Unsupported)]
    [InlineData(InvestigationOutcome.InsufficientEvidence)]
    public async Task NonSuccessfulAssessmentDoesNotWrite(InvestigationOutcome outcome)
    {
        RecordingMemoryClient client = new();
        InvestigationMemoryCoordinator coordinator = new(
            client,
            new FixedTimeProvider(TestData.Now));

        MemoryWriteOutcome write = await coordinator.SaveAsync(
            TestData.Result(outcome, []),
            CancellationToken.None);

        Assert.Equal(MemoryWriteStatus.SkippedNonSuccessfulOutcome, write.Status);
        Assert.Null(write.Record);
        Assert.Null(write.SerializedRecord);
        Assert.Empty(client.Records);
    }

    [Fact]
    public async Task DuplicateMemorySearchFailsBeforeWrite()
    {
        RecordingMemoryClient client = new();
        InvestigationMemoryCoordinator coordinator = new(
            client,
            new FixedTimeProvider(TestData.Now));
        TrustedToolExecution search = new(
            "call-1",
            Demo4MemoryContract.SearchToolName,
            true,
            "{}");

        await Assert.ThrowsAsync<UnsafeMemoryReferenceException>(() =>
            coordinator.SaveAsync(
                TestData.Result(
                    InvestigationOutcome.Completed,
                    [search, search with { CallId = "call-2" }]),
                CancellationToken.None));
        Assert.Empty(client.Records);
    }

    [Fact]
    public async Task RefusedRequestCannotReadMemory()
    {
        RecordingMemoryClient client = new();
        InvestigationMemoryCoordinator coordinator = new(
            client,
            new FixedTimeProvider(TestData.Now));

        await Assert.ThrowsAsync<UnsafeMemoryReferenceException>(() =>
            coordinator.SaveAsync(
                TestData.Result(
                    InvestigationOutcome.Refused,
                    [
                        new(
                            "call-1",
                            Demo4MemoryContract.SearchToolName,
                            true,
                            "{}")
                    ]),
                CancellationToken.None));
        Assert.Empty(client.Records);
    }

    private sealed class RecordingMemoryClient : IFoundryMemoryItemClient
    {
        public List<NotebookRecordEnvelope> Records { get; } = [];

        public Task CreateAndVerifyAsync(
            NotebookRecordEnvelope record,
            string serializedRecord,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(serializedRecord, NotebookRecordCodec.Serialize(record));
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<MemoryNotebookListing> ListValidRecordsAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new MemoryNotebookListing(
                Records.Select(record => new MemoryNotebookEntry(
                    $"memory-{record.RecordId}",
                    now,
                    record)).ToArray(),
                Records.Count,
                0));
        }
    }
}
